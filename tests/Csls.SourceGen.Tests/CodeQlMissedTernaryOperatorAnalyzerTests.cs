using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies the local guard for CodeQL missed-ternary-operator findings.
/// </summary>
[TestClass]
public sealed class CodeQlMissedTernaryOperatorAnalyzerTests(TestContext testContext)
{
    /// <summary>
    /// Verifies two branches assigning the same target are rejected.
    /// </summary>
    [TestMethod]
    public async Task ReportsSameTargetBranchAssignments()
    {
        const string Source = """
            internal static class Projection
            {
                internal static int Select(bool condition, int value)
                {
                    int result;
                    if (condition)
                    {
                        result = 0;
                    }
                    else
                    {
                        result = value;
                    }

                    return result;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source)
            .ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlMissedTernaryOperatorAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Contains(
            "result",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Verifies branches with different targets or additional work remain accepted.
    /// </summary>
    [TestMethod]
    public async Task AcceptsDistinctTargetsAndMultiStatementBranches()
    {
        const string Source = """
            internal static class Projection
            {
                internal static int Select(bool condition, int value)
                {
                    int left = 0;
                    int right = 0;
                    if (condition)
                    {
                        left = value;
                    }
                    else
                    {
                        right = value;
                    }

                    if (condition)
                    {
                        left = 1;
                        right = 2;
                    }
                    else
                    {
                        left = 3;
                    }

                    return left + right;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source)
            .ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies matching discarded values are rejected, including awaited task-selection results.
    /// </summary>
    [TestMethod]
    [DataRow("_ = await Task.WhenAny(first, second);", "_ = await Task.WhenAny(first, second, third);")]
    [DataRow("_ = 1;", "_ = 2;")]
    public async Task ReportsSameTypeDiscardBranchAssignments(string whenTrue, string whenFalse)
    {
        string source = CreateDiscardSource(whenTrue, whenFalse);

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlMissedTernaryOperatorAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("'_'", diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.AreEqual(source.IndexOf("if (condition)", StringComparison.Ordinal), diagnostic.Location.SourceSpan.Start);
    }

    /// <summary>
    /// Verifies discards with incompatible types, different targets, or extra work remain accepted.
    /// </summary>
    [TestMethod]
    [DataRow("_ = 1;", "_ = \"value\";")]
    [DataRow("_ = 1;", "result = 2;")]
    [DataRow("_ = 1; System.Console.WriteLine(result);", "_ = 2;")]
    [DataRow("await first;", "await second;")]
    public async Task AcceptsBranchesWithoutEquivalentDiscardAssignments(string whenTrue, string whenFalse)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(CreateDiscardSource(whenTrue, whenFalse))
            .ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies assignment branches in an else-if chain follow the upstream query exclusion.
    /// </summary>
    [TestMethod]
    [DataRow("_")]
    [DataRow("result")]
    public async Task AcceptsAssignmentBranchesInElseIfChains(string target)
    {
        string source = $$"""
            internal static class Projection
            {
                internal static int Select(int condition)
                {
                    int result = 0;
                    if (condition == 0)
                    {
                        {{target}} = 1;
                    }
                    else if (condition == 1)
                    {
                        {{target}} = 2;
                    }
                    else
                    {
                        {{target}} = 3;
                    }

                    return result;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    private static string CreateDiscardSource(string whenTrue, string whenFalse) => $$"""
        using System.Threading.Tasks;
        internal static class Projection
        {
            internal static async Task<int> Observe(bool condition, Task first, Task second, Task third)
            {
                int result = 0;
                if (condition)
                {
                    {{whenTrue}}
                }
                else
                {
                    {{whenFalse}}
                }

                return result;
            }
        }
        """;

    private async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        string sourcePath = Path.Join(Path.GetTempPath(), $"csls-missed-ternary-{Guid.NewGuid():N}.cs");
        try
        {
            await File.WriteAllTextAsync(sourcePath, source, testContext.CancellationToken).ConfigureAwait(false);
            using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await AnalyzeSourceAsync(SourceText.From(stream, Encoding.UTF8), sourcePath).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    private async Task<ImmutableArray<Diagnostic>> AnalyzeSourceAsync(SourceText source, string sourcePath)
    {
        var parseOptions = new CSharpParseOptions(
            LanguageVersion.CSharp14,
            DocumentationMode.Diagnose);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            parseOptions,
            path: sourcePath,
            cancellationToken: testContext.CancellationToken);
        string trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException(
                "The runtime did not expose trusted platform assemblies.");
        IEnumerable<MetadataReference> references = trustedAssemblies
            .Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "AnalyzerInput",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.IsEmpty(compilation.GetDiagnostics(testContext.CancellationToken).Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error));
        ImmutableArray<DiagnosticAnalyzer> analyzers =
            [new CodeQlMissedTernaryOperatorAnalyzer()];
        var analysisOptions = new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), onAnalyzerException: null,
            concurrentAnalysis: true, logAnalyzerExecutionTime: true, reportSuppressedDiagnostics: false);

        return await compilation
            .WithAnalyzers(analyzers, analysisOptions)
            .GetAnalyzerDiagnosticsAsync(testContext.CancellationToken)
            .ConfigureAwait(false);
    }
}
