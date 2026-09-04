using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Globalization;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies the local guard for CodeQL constant-condition findings.
/// </summary>
[TestClass]
public sealed class CodeQlConstantConditionAnalyzerTests
{
    /// <summary>
    /// Verifies repeated null and non-null tests after an exiting guard are rejected.
    /// </summary>
    [TestMethod]
    public async Task ReportsNullTestsMadeConstantByGuard()
    {
        const string Source = """
            internal static class Projection
            {
                internal static int Convert(string? value)
                {
                    if (value is not null)
                    {
                        return value.Length;
                    }

                    try
                    {
                        return value is null ? 0 : value.Length;
                    }
                    catch
                    {
                        return value is not null ? 1 : 2;
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source)
            .ConfigureAwait(false);

        Assert.HasCount(2, diagnostics);
        Assert.IsTrue(diagnostics.All(static diagnostic =>
            diagnostic.Id == CodeQlConstantConditionAnalyzer.DiagnosticId));
        Assert.Contains(
            "true",
            diagnostics[0].GetMessage(CultureInfo.InvariantCulture));
        Assert.Contains(
            "false",
            diagnostics[1].GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Verifies independent null tests and guards that do not exit remain accepted.
    /// </summary>
    [TestMethod]
    public async Task AcceptsNullTestsWithoutDominatingExit()
    {
        const string Source = """
            internal static class Projection
            {
                internal static int Convert(string? value)
                {
                    if (value is not null)
                    {
                        value = null;
                    }

                    return value is null ? 0 : value.Length;
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
            [new CodeQlConstantConditionAnalyzer()];

        return await compilation
            .WithAnalyzers(analyzers)
            .GetAnalyzerDiagnosticsAsync()
            .ConfigureAwait(false);
    }
}
