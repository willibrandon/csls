using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Globalization;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies nullable-property capture diagnostics against real Roslyn compilations.
/// </summary>
[TestClass]
public sealed class CodeQlNullablePropertyAnalyzerTests
{
    /// <summary>
    /// Gets the framework-owned cancellation context.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Rejects nullable property unwrapping after assertion-only checks, including alternate receiver syntax.
    /// </summary>
    [TestMethod]
    [DataRow("Current.Value")]
    [DataRow("this.Current.Value")]
    [DataRow("(this.Current).Value")]
    [DataRow("Current!.Value")]
    public async Task ReportsNullablePropertyUnwrappingAfterAssertion(string expression)
    {
        string source = $$"""
            #nullable enable
            using Microsoft.VisualStudio.TestTools.UnitTesting;
            internal sealed class Reader
            {
                internal int? Current { get; }
                internal int Read()
                {
                    Assert.IsNotNull(Current);
                    return {{expression}};
                }
            }
            """;
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlDereferencedValueMayBeNullAnalyzer.NullablePropertyDiagnosticId, diagnostic.Id);
        Assert.AreEqual(expression, source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
        Assert.Contains("Current", diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    /// <summary>
    /// Requires a captured value even when a previous property read was explicitly checked.
    /// </summary>
    [TestMethod]
    [DataRow("return Current.Value;")]
    [DataRow("if (Current.HasValue) return Current.Value; return -1;")]
    [DataRow("if (Current is not null) return Current.Value; return -1;")]
    public async Task ReportsUncapturedNullablePropertyReads(string body)
    {
        string source = $$"""
            #nullable enable
            internal sealed class Reader
            {
                internal int? Current { get; }
                internal int Read() { {{body}} }
            }
            """;
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlDereferencedValueMayBeNullAnalyzer.NullablePropertyDiagnosticId, diagnostic.Id);
        Assert.AreEqual("Current.Value", source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
    }

    /// <summary>
    /// Accepts one-read nullable captures, guarded locals, and ordinary properties named Value.
    /// </summary>
    [TestMethod]
    [DataRow("return Current ?? throw new System.InvalidOperationException();")]
    [DataRow("if (Current is int value) return value; return -1;")]
    [DataRow("int? value = Current; if (value.HasValue) return value.Value; return -1;")]
    [DataRow("return this.Value;")]
    public async Task AcceptsCapturedValuesAndNonNullableProperties(string body)
    {
        string source = $$"""
            #nullable enable
            internal sealed class Reader
            {
                internal int? Current { get; }
                internal int Value { get; }
                internal int Read() { {{body}} }
            }
            """;
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);
        Assert.IsEmpty(diagnostics);
    }

    private async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var options = new CSharpParseOptions(LanguageVersion.CSharp14);
        SyntaxTree syntax = CSharpSyntaxTree.ParseText(source, options, path: "Input.cs", cancellationToken: TestContext.CancellationToken);
        string assemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException("The runtime did not expose trusted platform assemblies.");
        IEnumerable<MetadataReference> references = assemblies.Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("NullableCaptureInput", [syntax], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.IsEmpty(compilation.GetDiagnostics(TestContext.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var analyzerOptions = new CompilationWithAnalyzersOptions(options: new AnalyzerOptions([]), onAnalyzerException: null,
            concurrentAnalysis: true, logAnalyzerExecutionTime: false);
        return await compilation.WithAnalyzers([new CodeQlDereferencedValueMayBeNullAnalyzer()], analyzerOptions)
            .GetAnalyzerDiagnosticsAsync(TestContext.CancellationToken).ConfigureAwait(false);
    }
}
