using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Globalization;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies the local guard for CodeQL missed-ternary-operator findings.
/// </summary>
[TestClass]
public sealed class CodeQlMissedTernaryOperatorAnalyzerTests
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

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var parseOptions = new CSharpParseOptions(
            LanguageVersion.CSharp14,
            DocumentationMode.Diagnose);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            parseOptions,
            path: "Input.cs");
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
        ImmutableArray<DiagnosticAnalyzer> analyzers =
            [new CodeQlMissedTernaryOperatorAnalyzer()];

        return await compilation
            .WithAnalyzers(analyzers)
            .GetAnalyzerDiagnosticsAsync()
            .ConfigureAwait(false);
    }
}
