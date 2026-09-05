using Microsoft.CodeAnalysis;
using System.Collections.Immutable;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies accidental field hiding and deliberate access to distinct physical base storage.
/// </summary>
/// <param name="testContext">The test cancellation context.</param>
[TestClass]
public sealed class CodeQlFieldMasksBaseFieldAnalyzerTests(TestContext testContext)
{
    /// <summary>
    /// Rejects visible fields masking base declarations regardless of explicit new syntax.
    /// </summary>
    /// <param name="modifier">The hiding declaration modifiers.</param>
    [TestMethod]
    [DataRow("internal")]
    [DataRow("internal new")]
    [DataRow("protected new")]
    public async Task ReportsAccidentalBaseFieldMasking(string modifier)
    {
        string source = $$"""
            internal class Base<T> { internal T _value; }
            internal class Middle : Base<int> { }
            internal class Derived : Middle { {{modifier}} int _value; }
            """;
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlFieldMasksBaseFieldAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual(source.LastIndexOf("_value", StringComparison.Ordinal), diagnostic.Location.SourceSpan.Start);
        Assert.AreEqual("_value".Length, diagnostic.Location.SourceSpan.Length);
    }

    /// <summary>
    /// Preserves private or static storage, distinct names, and intentional base access across partial declarations.
    /// </summary>
    /// <param name="baseField">The base field declaration.</param>
    /// <param name="derivedField">The derived field declaration.</param>
    /// <param name="body">The second partial declaration's members.</param>
    [TestMethod]
    [DataRow("internal T _value;", "internal new int _value;", "internal Derived() { base._value = 11; _value = 22; }")]
    [DataRow("private T _value;", "internal int _value;", "")]
    [DataRow("internal T _value;", "private new int _value;", "")]
    [DataRow("internal static T _value;", "internal new int _value;", "")]
    [DataRow("internal T _value;", "internal new static int _value;", "")]
    [DataRow("internal T _value;", "internal int _other;", "")]
    public async Task AcceptsDistinctOrExplicitBaseStorage(string baseField, string derivedField, string body)
    {
        string source = $$"""
            internal class Base<T> { {{baseField}} }
            internal partial class Derived : Base<int> { {{derivedField}} }
            internal partial class Derived { {{body}} }
            """;
        Assert.IsEmpty(await AnalyzeAsync(source).ConfigureAwait(false));
    }

    /// <summary>
    /// Requires access to the masked field rather than any unrelated base member.
    /// </summary>
    [TestMethod]
    public async Task RejectsUnrelatedBaseFieldAccess()
    {
        const string Source = """
            internal class Base { internal int _value; internal int _other; }
            internal class Derived : Base
            {
                internal new int _value;
                internal Derived() { base._other = 11; _value = 22; }
            }
            """;
        Diagnostic diagnostic = Assert.ContainsSingle(await AnalyzeAsync(Source).ConfigureAwait(false));
        Assert.AreEqual(CodeQlFieldMasksBaseFieldAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(Source.IndexOf("_value;", Source.IndexOf("class Derived", StringComparison.Ordinal),
            StringComparison.Ordinal), diagnostic.Location.SourceSpan.Start);
    }

    private Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source) => CodeQlFileCompilation.AnalyzeAsync(
        source, new CodeQlFieldMasksBaseFieldAnalyzer(), testContext.CancellationToken);
}
