using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Globalization;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies the local guard for CodeQL unused-collection findings.
/// </summary>
[TestClass]
public sealed class CodeQlUnusedCollectionAnalyzerTests
{
    /// <summary>
    /// Verifies a mutation-only collection alias is rejected before CodeQL runs.
    /// </summary>
    [TestMethod]
    public async Task ReportsMutationOnlyDeconstructionAlias()
    {
        const string Source = """
            using System.Collections.Generic;

            internal static class Formatter
            {
                internal static void Append((List<string> Values, int Depth) state)
                {
                    (List<string> values, int depth) = state;
                    values.Add(depth.ToString());
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlUnusedCollectionAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Contains(
            "values",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Verifies reading through a deconstructed collection alias satisfies the rule.
    /// </summary>
    [TestMethod]
    public async Task AcceptsDeconstructionAliasWhoseContentsAreRead()
    {
        const string Source = """
            using System.Collections.Generic;

            internal static class Formatter
            {
                internal static string Append((List<string> Values, int Depth) state)
                {
                    (List<string> values, int depth) = state;
                    values.Add(depth.ToString());
                    return string.Join(", ", values);
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

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
            [new CodeQlUnusedCollectionAnalyzer()];

        return await compilation
            .WithAnalyzers(analyzers)
            .GetAnalyzerDiagnosticsAsync()
            .ConfigureAwait(false);
    }
}
