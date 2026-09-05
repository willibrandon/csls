using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies the local guard for CodeQL constant-condition findings.
/// </summary>
[TestClass]
public sealed class CodeQlConstantConditionAnalyzerTests(TestContext testContext)
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

    /// <summary>
    /// Verifies short-circuit null tests correlated with a conditional initializer are rejected.
    /// </summary>
    [TestMethod]
    [DataRow("value is null ? null : new()", "value is not null && output is not null", "output is not null", "true")]
    [DataRow("value is null ? null : new()", "value is not null && output is null", "output is null", "false")]
    [DataRow("value is not null ? new() : null", "value is null && output is null", "output is null", "true")]
    [DataRow("value is not null ? new() : null", "value is null && output is not null", "output is not null", "false")]
    [DataRow("value is null ? null : new()", "value is null || output is not null", "output is not null", "true")]
    [DataRow("value is null ? null : new()", "value is not null || output is not null", "output is not null", "false")]
    [DataRow("(value is null) ? null : new System.Text.StringBuilder()", "(value is not null) && (output is not null)", "output is not null", "true")]
    public async Task ReportsNullTestCorrelatedWithConditionalInitializer(
        string initializer,
        string condition,
        string expectedExpression,
        string expectedValue)
    {
        string source = $$"""
            internal static class Projection
            {
                internal static async System.Threading.Tasks.Task<bool> Convert(string? value)
                {
                    System.Text.StringBuilder? output = {{initializer}};
                    await System.Threading.Tasks.Task.Yield();
                    System.Console.Write(output);
                    return {{condition}};
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlConstantConditionAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains($"'{expectedValue}'", diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.IsNotNull(diagnostic.Location.SourceTree);
        SourceText text = await diagnostic.Location.SourceTree.GetTextAsync(testContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(expectedExpression, text.ToString(diagnostic.Location.SourceSpan));
    }

    /// <summary>
    /// Verifies writes, references, and captured variables invalidate initializer correlations.
    /// </summary>
    [TestMethod]
    [DataRow("value = null;")]
    [DataRow("output = null;")]
    [DataRow("Change(ref value);")]
    [DataRow("Change(ref output);")]
    [DataRow("System.Action change = () => value = null; change();")]
    [DataRow("System.Action change = () => output = null; change();")]
    [DataRow("ref string? alias = ref value; alias = null;")]
    public async Task AcceptsCorrelatedNullTestsAfterMutation(string mutation)
    {
        string source = $$"""
            internal static class Projection
            {
                internal static bool Convert(string? value)
                {
                    System.Text.StringBuilder? output = value is null ? null : new();
                    {{mutation}}
                    return value is not null && output is not null;
                }

                private static void Change<T>(ref T? value) where T : class => value = null;
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies mutable receivers, unrelated guards, and unknown initializer values remain accepted.
    /// </summary>
    [TestMethod]
    [DataRow("string? value, string? other", "value is null ? null : Create()", "value is not null && output is not null")]
    [DataRow("string? value, string? other", "value is null ? null : new()", "other is not null && output is not null")]
    [DataRow("ref string? value, string? other", "value is null ? null : new()", "value is not null && output is not null")]
    [DataRow("string? value, string? other", "Current is null ? null : new()", "Current is not null && output is not null")]
    [DataRow("string? value, string? other", "value is null ? null : new((value = null) ?? string.Empty)", "value is not null && output is not null")]
    public async Task AcceptsUnprovenConditionalNullCorrelations(string parameters, string initializer, string condition)
    {
        string source = $$"""
            internal static class Projection
            {
                private static string? Current => System.Console.ReadLine();

                internal static bool Convert({{parameters}})
                {
                    System.Text.StringBuilder? output = {{initializer}};
                    return {{condition}};
                }

                private static System.Text.StringBuilder? Create() => null;
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies construction does not imply non-null storage after nullable boxing or user conversions.
    /// </summary>
    [TestMethod]
    [DataRow("object?", "new int?()")]
    [DataRow("string?", "new Convertible()")]
    public async Task AcceptsConstructedValuesThatCanConvertToNull(string type, string creation)
    {
        string source = $$"""
            internal static class Projection
            {
                internal static bool Convert(string? value)
                {
                    {{type}} output = value is null ? null : {{creation}};
                    return value is not null && output is not null;
                }
            }

            internal sealed class Convertible
            {
                public static implicit operator string?(Convertible value) => null;
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    private async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        string sourcePath = Path.Join(Path.GetTempPath(), $"csls-constant-condition-{Guid.NewGuid():N}.cs");
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
            [new CodeQlConstantConditionAnalyzer()];
        var analysisOptions = new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), onAnalyzerException: null,
            concurrentAnalysis: true, logAnalyzerExecutionTime: true, reportSuppressedDiagnostics: false);

        return await compilation
            .WithAnalyzers(analyzers, analysisOptions)
            .GetAnalyzerDiagnosticsAsync(testContext.CancellationToken)
            .ConfigureAwait(false);
    }
}
