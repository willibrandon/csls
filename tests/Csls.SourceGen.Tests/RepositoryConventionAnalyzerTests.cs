using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Globalization;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies repository diagnostics by running the analyzer through a real Roslyn compilation.
/// </summary>
[TestClass]
public sealed class RepositoryConventionAnalyzerTests
{
    /// <summary>
    /// Verifies every additional type declaration is rejected.
    /// </summary>
    [TestMethod]
    public async Task ReportsEveryAdditionalTypeInAFile()
    {
        const string Source = """
            /// <summary>
            /// Represents the first type.
            /// </summary>
            internal sealed class FirstType;

            /// <summary>
            /// Represents the second type.
            /// </summary>
            internal sealed class SecondType;
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(RepositoryConventionAnalyzer.OneTypePerFileDiagnosticId, diagnostic.Id);
        Assert.Contains("SecondType", diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Verifies undocumented internal members are rejected.
    /// </summary>
    [TestMethod]
    public async Task ReportsUndocumentedInternalMember()
    {
        const string Source = """
            /// <summary>
            /// Represents a documented type.
            /// </summary>
            internal sealed class DocumentedType
            {
                internal void UndocumentedMethod()
                {
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(RepositoryConventionAnalyzer.XmlDocumentationDiagnosticId, diagnostic.Id);
        Assert.Contains(
            "UndocumentedMethod",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Verifies single-line summaries are rejected.
    /// </summary>
    [TestMethod]
    public async Task ReportsSingleLineSummary()
    {
        const string Source = """
            /// <summary>Represents an invalid summary.</summary>
            internal sealed class DocumentedType;
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(RepositoryConventionAnalyzer.ThreeLineSummaryDiagnosticId, diagnostic.Id);
    }

    /// <summary>
    /// Verifies a documented single-type file satisfies every repository diagnostic.
    /// </summary>
    [TestMethod]
    public async Task AcceptsDocumentedOneTypeFile()
    {
        const string Source = """
            /// <summary>
            /// Represents a documented type.
            /// </summary>
            internal sealed class DocumentedType
            {
                /// <summary>
                /// Performs documented work.
                /// </summary>
                internal void DocumentedMethod()
                {
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies compiler-generated top-level entry-point symbols require no documentation.
    /// </summary>
    [TestMethod]
    public async Task AcceptsTopLevelStatements()
    {
        const string Source = "Console.WriteLine(\"csls\");";

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            OutputKind.ConsoleApplication).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string source,
        OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary)
    {
        var parseOptions = new CSharpParseOptions(
            LanguageVersion.CSharp14,
            DocumentationMode.Diagnose);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions, path: "Input.cs");
        string trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException("The runtime did not expose trusted platform assemblies.");
        IEnumerable<MetadataReference> references = trustedAssemblies
            .Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "AnalyzerInput",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(outputKind));
        ImmutableArray<DiagnosticAnalyzer> analyzers =
            [new RepositoryConventionAnalyzer()];

        return await compilation
            .WithAnalyzers(analyzers)
            .GetAnalyzerDiagnosticsAsync()
            .ConfigureAwait(false);
    }
}
