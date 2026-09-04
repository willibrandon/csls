using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Globalization;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies the local guard for CodeQL useless-upcast findings.
/// </summary>
[TestClass]
public sealed class CodeQlUselessUpcastAnalyzerTests
{
    /// <summary>
    /// Verifies an implicit reference conversion nested inside another cast is rejected.
    /// </summary>
    [TestMethod]
    public async Task ReportsRedundantNestedUpcast()
    {
        const string Source = """
            internal static class Projection
            {
                internal static T Convert<T>(int[] values) => (T)(object)values;
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source)
            .ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlUselessUpcastAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Contains(
            "object",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Verifies the direct generic conversion does not trigger the rule.
    /// </summary>
    [TestMethod]
    public async Task AcceptsPatternValidatedGenericConversion()
    {
        const string Source = """
            internal static class Projection
            {
                internal static T Convert<T>(int[] values) => values is T value
                    ? value
                    : throw new System.InvalidOperationException();
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
            [new CodeQlUselessUpcastAnalyzer()];

        return await compilation
            .WithAnalyzers(analyzers)
            .GetAnalyzerDiagnosticsAsync()
            .ConfigureAwait(false);
    }
}
