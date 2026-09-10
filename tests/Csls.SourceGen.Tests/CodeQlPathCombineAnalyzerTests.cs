using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies path composition diagnostics use resolved API identities for every input shape.
/// </summary>
/// <param name="testContext">The running test's cooperative cancellation context.</param>
[TestClass]
public sealed class CodeQlPathCombineAnalyzerTests(TestContext testContext)
{
    /// <summary>
    /// Verifies all path-combination overloads and import forms fail independently of argument contents.
    /// </summary>
    /// <param name="expression">The actual framework call compiled from a physical source file.</param>
    [TestMethod]
    [DataRow("Path.Combine(root, child)")]
    [DataRow("Path.Combine(root, \"file.cs\")")]
    [DataRow("Path.Combine(\"/base\", \"/replacement\")")]
    [DataRow("Path.Combine(root, child, \"file.cs\")")]
    [DataRow("Path.Combine(root, child, \"nested\", \"file.cs\")")]
    [DataRow("Path.Combine(new string[] { root, child })")]
    [DataRow("Path.Combine(path2: child, path1: root)")]
    [DataRow("global::System.IO.Path.Combine(root, child)")]
    [DataRow("IOPath.Combine(root, child)")]
    [DataRow("Combine(root, child)")]
    public async Task ReportsPathCombineCall(string expression)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync($"return {expression};")
            .ConfigureAwait(false);

        await AssertDiagnosticAsync(diagnostics, expression).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies the fixed-prefix interpolated temporary filename still follows the framework API rule.
    /// </summary>
    [TestMethod]
    public async Task ReportsFixedPrefixTemporaryFilename()
    {
        const string Expression = "Path.Combine(Path.GetTempPath(), $\"csls-input-{Guid.NewGuid():N}.cs\")";
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync($"return {Expression};")
            .ConfigureAwait(false);

        await AssertDiagnosticAsync(diagnostics, Expression).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies a rooted-path rejection does not hide calls selected by the upstream API rule.
    /// </summary>
    [TestMethod]
    public async Task ReportsGuardedPathCombineCall()
    {
        const string Expression = "Path.Combine(root, child)";
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            "if (Path.IsPathRooted(child)) { throw new ArgumentException(nameof(child)); } " +
            $"return {Expression};").ConfigureAwait(false);

        await AssertDiagnosticAsync(diagnostics, Expression).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies component-preserving composition and other path operations remain accepted.
    /// </summary>
    /// <param name="expression">The framework expression that does not call Path.Combine.</param>
    [TestMethod]
    [DataRow("Path.Join(root, child)")]
    [DataRow("IOPath.Join(root, child, \"file.cs\")")]
    [DataRow("Path.GetFullPath(child, root)")]
    [DataRow("nameof(Path.Combine)")]
    public async Task AcceptsOtherPathOperations(string expression)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync($"return {expression};")
            .ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies unrelated methods with the same method and type names are not framework calls.
    /// </summary>
    [TestMethod]
    public async Task AcceptsUnrelatedCombineMethod()
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            "return Path.Combine(root, child);",
            "internal static class Path { internal static string Combine(string root, string child) => string.Concat(root, child); }")
            .ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    private async Task AssertDiagnosticAsync(ImmutableArray<Diagnostic> diagnostics, string expression)
    {
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlPathCombineAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual(
            "Use Path.Join instead of Path.Combine to preserve preceding path components",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.IsNotNull(diagnostic.Location.SourceTree);
        SourceText text = await diagnostic.Location.SourceTree.GetTextAsync(testContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(expression, text.ToString(diagnostic.Location.SourceSpan));
    }

    private async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string body, string additionalMember = "")
    {
        string source = $$"""
            using System;
            using System.IO;
            using IOPath = System.IO.Path;
            using static System.IO.Path;
            namespace Csls.AnalyzerInput;
            internal static class PathOperations
            {
                internal static string Compose(string root, string child)
                {
                    {{body}}
                }
                {{additionalMember}}
            }
            """;
        string sourcePath = Path.Join(Path.GetTempPath(), $"csls-path-combine-{Guid.NewGuid():N}.cs");
        try
        {
            await File.WriteAllTextAsync(sourcePath, source, testContext.CancellationToken).ConfigureAwait(false);
            SyntaxTree syntaxTree;
            using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var text = SourceText.From(stream, Encoding.UTF8);
                syntaxTree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.CSharp14),
                    path: sourcePath, cancellationToken: testContext.CancellationToken);
            }

            string trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
                ?? throw new InvalidOperationException("The runtime did not expose trusted platform assemblies.");
            IEnumerable<MetadataReference> references = trustedAssemblies.Split(Path.PathSeparator)
                .Select(static path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("AnalyzerInput", [syntaxTree], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.IsEmpty(compilation.GetDiagnostics(testContext.CancellationToken).Where(static diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error));

            var analysisOptions = new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), onAnalyzerException: null,
                concurrentAnalysis: true, logAnalyzerExecutionTime: true, reportSuppressedDiagnostics: false);
            return await compilation.WithAnalyzers([new CodeQlPathCombineAnalyzer()], analysisOptions)
                .GetAnalyzerDiagnosticsAsync(testContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }
}
