using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Globalization;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies the local guard for CodeQL useless-assignment-to-local findings.
/// </summary>
[TestClass]
public sealed class CodeQlUselessAssignmentToLocalAnalyzerTests
{
    /// <summary>
    /// Verifies a final constant write to a local is rejected.
    /// </summary>
    [TestMethod]
    public async Task ReportsUnreadFinalConstantAssignment()
    {
        const string Source = """
            internal static class Projection
            {
                internal static void Release(nint pointer)
                {
                    nint completed = pointer;
                    Consume(completed);
                    completed = 0;
                }

                private static void Consume(nint value) { }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source)
            .ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(
            CodeQlUselessAssignmentToLocalAnalyzer.DiagnosticId,
            diagnostic.Id);
        Assert.Contains(
            "completed",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Verifies observed assignments and side-effecting expressions remain accepted.
    /// </summary>
    [TestMethod]
    public async Task AcceptsObservedOrSideEffectingAssignments()
    {
        const string Source = """
            internal static class Projection
            {
                internal static void Release(nint pointer)
                {
                    pointer = 0;
                    Consume(pointer);
                    pointer = GetPointer();
                }

                private static nint GetPointer() => 0;
                private static void Consume(nint value) { }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source)
            .ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies an unread declaration assignment is rejected while preserving its initializer.
    /// </summary>
    [TestMethod]
    public async Task ReportsUnreadDeclarationAssignment()
    {
        const string Source = """
            internal static class Projection
            {
                internal static int Read()
                {
                    bool success = TryRead(out int value);
                    return value;
                }

                private static bool TryRead(out int value)
                {
                    value = 1;
                    return true;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source)
            .ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(
            CodeQlUselessAssignmentToLocalAnalyzer.DiagnosticId,
            diagnostic.Id);
        Assert.Contains(
            "success",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Verifies a declaration assignment read later remains accepted.
    /// </summary>
    [TestMethod]
    public async Task AcceptsObservedDeclarationAssignment()
    {
        const string Source = """
            internal static class Projection
            {
                internal static bool Read()
                {
                    bool success = TryRead();
                    return success;
                }

                private static bool TryRead() => true;
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source)
            .ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies a using declaration remains accepted because disposal observes its value.
    /// </summary>
    [TestMethod]
    public async Task AcceptsUsingDeclarationLifetime()
    {
        const string Source = """
            internal static class Projection
            {
                internal static void Read()
                {
                    using var stream = new System.IO.MemoryStream();
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
            [new CodeQlUselessAssignmentToLocalAnalyzer()];

        return await compilation
            .WithAnalyzers(analyzers)
            .GetAnalyzerDiagnosticsAsync()
            .ConfigureAwait(false);
    }
}
