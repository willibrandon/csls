using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies the local guard for CodeQL missed-Where findings.
/// </summary>
[TestClass]
public sealed class CodeQlMissedWhereAnalyzerTests
{
    /// <summary>
    /// Verifies a loop that conditionally returns from its only branch is rejected.
    /// </summary>
    [TestMethod]
    public async Task ReportsConditionalReturnFilter()
    {
        const string Source = """
            using System.Collections.Generic;

            internal static class Projection
            {
                internal static bool ContainsPositive(IEnumerable<int> values)
                {
                    foreach (int value in values)
                    {
                        if (value > 0)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source)
            .ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlMissedWhereAnalyzer.DiagnosticId, diagnostic.Id);
    }

    /// <summary>
    /// Verifies explicit sequence filtering remains accepted.
    /// </summary>
    [TestMethod]
    public async Task AcceptsExplicitWhereFilter()
    {
        const string Source = """
            using System.Collections.Generic;
            using System.Linq;

            internal static class Projection
            {
                internal static bool ContainsPositive(IEnumerable<int> values)
                {
                    foreach (int value in values.Where(static value => value > 0))
                    {
                        return true;
                    }

                    return false;
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
        ImmutableArray<DiagnosticAnalyzer> analyzers = [new CodeQlMissedWhereAnalyzer()];

        return await compilation
            .WithAnalyzers(analyzers)
            .GetAnalyzerDiagnosticsAsync()
            .ConfigureAwait(false);
    }
}
