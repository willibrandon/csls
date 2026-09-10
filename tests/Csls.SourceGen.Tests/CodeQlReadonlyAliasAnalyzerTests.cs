using Microsoft.CodeAnalysis;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies readonly diagnostics distinguish writable aliases from value and readonly-reference reads.
/// </summary>
/// <param name="testContext">The running test's cancellation context.</param>
[TestClass]
public sealed class CodeQlReadonlyAliasAnalyzerTests(TestContext testContext)
{
    /// <summary>
    /// Preserves fields exposed through writable local aliases, reference assignments, and reference returns.
    /// </summary>
    /// <param name="member">The member that exposes or mutates the field storage.</param>
    [TestMethod]
    [DataRow("internal void Update() { ref int slot = ref _value; slot++; }")]
    [DataRow("internal void Update(bool choose, ref int other) { ref int slot = ref (choose ? ref _value : ref other); slot++; }")]
    [DataRow("internal void Update(bool choose, ref int other) { ref int slot = ref (choose ? ref other : ref _value); slot++; }")]
    [DataRow("internal void Update(bool first, bool second, ref int other) { ref int slot = ref (first ? ref other : ref (second ? ref _value : ref other)); slot++; }")]
    [DataRow("internal void Update(ref int other) { ref int slot = ref other; slot = ref _value; slot++; }")]
    [DataRow("internal ref int GetValue() => ref _value;")]
    [DataRow("internal ref int GetValue() { return ref _value; }")]
    [DataRow("internal ref int Value => ref _value;")]
    public async Task AcceptsWritableFieldAliases(string member)
    {
        Assert.IsEmpty(await CodeQlFileCompilation.AnalyzeAsync(Source(member),
            new CodeQlMissedReadonlyModifierAnalyzer(), testContext.CancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Retains readonly enforcement for copied values, readonly aliases, and conditional predicates.
    /// </summary>
    /// <param name="member">The member that reads the field without exposing writable storage.</param>
    [TestMethod]
    [DataRow("internal int Read() { int slot = _value; return ++slot; }")]
    [DataRow("internal int Read() { ref readonly int slot = ref _value; return slot; }")]
    [DataRow("internal ref readonly int Read() => ref _value;")]
    [DataRow("internal int Read(ref int first, ref int second) { ref int slot = ref (_value == 0 ? ref first : ref second); return ++slot; }")]
    public async Task ReportsReadOnlyFieldAccess(string member)
    {
        string source = Source(member);
        Diagnostic diagnostic = Assert.ContainsSingle(await CodeQlFileCompilation.AnalyzeAsync(source,
            new CodeQlMissedReadonlyModifierAnalyzer(), testContext.CancellationToken).ConfigureAwait(false));

        Assert.AreEqual(CodeQlMissedReadonlyModifierAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual("_value", source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
    }

    private static string Source(string member) => $$"""
        internal sealed class Reader
        {
            private int _value = 42;
            {{member}}
        }
        """;
}
