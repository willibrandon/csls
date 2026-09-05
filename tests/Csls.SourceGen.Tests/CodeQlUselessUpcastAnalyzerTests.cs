using Microsoft.CodeAnalysis;
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
    /// Gets the test cancellation context used for file-backed receiver fixtures.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Reports each class receiver upcast shape found during debugger fixture analysis.
    /// </summary>
    /// <param name="expression">The source receiver expression.</param>
    [TestMethod]
    [DataRow("((Base)value)._value")]
    [DataRow("((Base)value).Read()")]
    [DataRow("((Base)value).VirtualRead()")]
    [DataRow("(((Base)value)).Read()")]
    public async Task ReportsClassReceiverUpcast(string expression)
    {
        string source = $$"""
            internal class Base { internal int _value; internal int Read() => _value; internal virtual int VirtualRead() => _value; }
            internal class Derived : Base { }
            internal static class Reader { internal static int Read(Derived value) => {{expression}}; }
            """;
        Diagnostic diagnostic = Assert.ContainsSingle(await CodeQlFileCompilation.AnalyzeAsync(
            source, new CodeQlUselessUpcastAnalyzer(), TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(CodeQlUselessUpcastAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual(source.IndexOf("(Base)value", StringComparison.Ordinal), diagnostic.Location.SourceSpan.Start);
        Assert.AreEqual("(Base)value".Length, diagnostic.Location.SourceSpan.Length);
    }

    /// <summary>
    /// Retains interface selection, runtime downcasts, numeric conversions, and overload-disambiguating arguments.
    /// </summary>
    /// <param name="expression">The source expression with meaningful conversion semantics.</param>
    [TestMethod]
    [DataRow("((IReader)value).Read()")]
    [DataRow("((Derived)parent).Read()")]
    [DataRow("((long)number).GetHashCode()")]
    [DataRow("Pick((Base)value)")]
    public async Task AcceptsMeaningfulReceiverAndArgumentConversions(string expression)
    {
        string source = $$"""
            internal interface IReader { int Read(); }
            internal class Base { public int Read() => 1; }
            internal class Derived : Base, IReader { }
            internal static class Reader
            {
                internal static int Pick(Base value) => 1;
                internal static int Pick(Derived value) => 2;
                internal static int Read(Derived value, Base parent, int number) => {{expression}};
            }
            """;
        Assert.IsEmpty(await CodeQlFileCompilation.AnalyzeAsync(source, new CodeQlUselessUpcastAnalyzer(),
            TestContext.CancellationToken).ConfigureAwait(false));
    }

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

    /// <summary>
    /// Verifies an explicit null upcast in a typed declaration is rejected.
    /// </summary>
    [TestMethod]
    public async Task ReportsRedundantNullUpcast()
    {
        const string Source = """
            internal static class Projection
            {
                internal static string? Convert()
                {
                    string? value = (string?)null;
                    return value;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source)
            .ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlUselessUpcastAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Contains(
            "string",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    private Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source) => CodeQlFileCompilation.AnalyzeAsync(
        source, new CodeQlUselessUpcastAnalyzer(), TestContext.CancellationToken);
}
