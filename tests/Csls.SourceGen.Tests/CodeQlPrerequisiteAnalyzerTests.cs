using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies local diagnostics for prerequisite cache conditions and asynchronous package selection.
/// </summary>
[TestClass]
public sealed class CodeQlPrerequisiteAnalyzerTests
{
    /// <summary>
    /// Gets the cancellation token for physical source-file analysis.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Rejects combined optional clipboard and Vulkan cache checks at the reported complexity boundary.
    /// </summary>
    [TestMethod]
    public async Task ReportsCombinedPortableCacheCondition()
    {
        const string Member = """
            internal static bool Ready(bool clipboard, bool vulkan, string clip, string library, string manifest) =>
                (!clipboard || File.Exists(clip)) && (!vulkan || File.Exists(library) && File.Exists(manifest));
            """;
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Member, new CodeQlComplexConditionAnalyzer())
            .ConfigureAwait(false);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlComplexConditionAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.IsNotNull(diagnostic.Location.SourceTree);
        SourceText source = await diagnostic.Location.SourceTree.GetTextAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(
            "(!clipboard || File.Exists(clip)) && (!vulkan || File.Exists(library) && File.Exists(manifest))",
            source.ToString(diagnostic.Location.SourceSpan));
    }

    /// <summary>
    /// Accepts separately named readiness checks without losing any required file check.
    /// </summary>
    [TestMethod]
    public async Task AcceptsNamedPortableCacheChecks()
    {
        const string Member = """
            internal static bool Ready(bool clipboard, bool vulkan, string clip, string library, string manifest)
            {
                bool clipboardReady = !clipboard || File.Exists(clip);
                bool libraryReady = !vulkan || File.Exists(library);
                bool manifestReady = !vulkan || File.Exists(manifest);
                return clipboardReady && libraryReady && manifestReady;
            }
            """;
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Member, new CodeQlComplexConditionAnalyzer())
            .ConfigureAwait(false);
        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Rejects a conditional first-match return even when its predicate is awaited.
    /// </summary>
    [TestMethod]
    public async Task ReportsAsynchronousFirstMatchFilter()
    {
        const string Member = """
            internal static async Task<string?> SelectAsync(string[] paths)
            {
                foreach (string path in paths)
                {
                    if (await ExistsAsync(path).ConfigureAwait(false))
                    {
                        return path;
                    }
                }
                return null;
            }
            private static async Task<bool> ExistsAsync(string path)
            {
                await Task.Yield();
                return File.Exists(path);
            }
            """;
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Member, new CodeQlMissedWhereAnalyzer())
            .ConfigureAwait(false);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlMissedWhereAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.IsNotNull(diagnostic.Location.SourceTree);
        SourceText source = await diagnostic.Location.SourceTree.GetTextAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.StartsWith("foreach (string path in paths)", source.ToString(diagnostic.Location.SourceSpan),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Accepts explicit asynchronous first-match selection with sequential predicate evaluation.
    /// </summary>
    [TestMethod]
    public async Task AcceptsAsynchronousFirstMatchOperator()
    {
        const string Member = """
            internal static async Task<string?> SelectAsync(string[] paths) =>
                await paths.ToAsyncEnumerable().FirstOrDefaultAsync(static async (path, _) =>
                {
                    await Task.Yield();
                    return File.Exists(path);
                }).ConfigureAwait(false);
            """;
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Member, new CodeQlMissedWhereAnalyzer())
            .ConfigureAwait(false);
        Assert.IsEmpty(diagnostics);
    }

    private async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string member, DiagnosticAnalyzer analyzer)
    {
        string sourcePath = Path.Join(Path.GetTempPath(), $"csls-prerequisite-analysis-{Guid.NewGuid():N}.cs");
        try
        {
            string source = $$"""
                using System.IO;
                using System.Linq;
                using System.Threading.Tasks;
                namespace Csls.AnalyzerInput;
                internal static class Prerequisites
                {
                    {{member}}
                }
                """;
            await File.WriteAllTextAsync(sourcePath, source, TestContext.CancellationToken).ConfigureAwait(false);
            SyntaxTree tree;
            using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var text = SourceText.From(stream, Encoding.UTF8);
                tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.CSharp14),
                    path: sourcePath, cancellationToken: TestContext.CancellationToken);
            }

            string trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
                ?? throw new InvalidOperationException("The runtime did not expose trusted platform assemblies.");
            IEnumerable<MetadataReference> references = trustedAssemblies.Split(Path.PathSeparator)
                .Select(static path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("AnalyzerInput", [tree], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.IsEmpty(compilation.GetDiagnostics(TestContext.CancellationToken).Where(static diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error));
            var options = new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), onAnalyzerException: null,
                concurrentAnalysis: true, logAnalyzerExecutionTime: true, reportSuppressedDiagnostics: false);
            return await compilation.WithAnalyzers([analyzer], options)
                .GetAnalyzerDiagnosticsAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }
}
