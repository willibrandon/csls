using Microsoft.CodeAnalysis;
using System.Collections.Immutable;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies shared-field writes against real file-backed C# compilations.
/// </summary>
/// <param name="testContext">The test cancellation context.</param>
[TestClass]
public sealed class CodeQlStaticFieldWrittenByInstanceAnalyzerTests(TestContext testContext)
{
    /// <summary>
    /// Rejects writes to the declaring type's static storage from instance members.
    /// </summary>
    /// <param name="member">The instance member containing one shared-field write.</param>
    [TestMethod]
    [DataRow("internal Sample(int number) { s_number = number; }")]
    [DataRow("internal void Set(int number) { s_number = number; }")]
    [DataRow("internal int Number { set { s_number = value; } }")]
    [DataRow("internal int Number { get { return s_number++; } }")]
    [DataRow("internal void Set(int number) { s_number += number; }")]
    [DataRow("internal void Set() { ++s_number; }")]
    [DataRow("internal void Set() { s_number--; }")]
    [DataRow("internal void Set() { (s_number, _) = (1, 2); }")]
    [DataRow("internal void Set() { System.Threading.Interlocked.Exchange(ref s_number, 1); }")]
    [DataRow("internal void Set() { int.TryParse(\"1\", out s_number); }")]
    public async Task ReportsInstanceWritesToSharedFields(string member)
    {
        string source = $$"""
            internal sealed class Sample
            {
                internal static int s_number;
                {{member}}
            }
            """;
        Diagnostic diagnostic = Assert.ContainsSingle(await AnalyzeAsync(source).ConfigureAwait(false));
        Assert.AreEqual(CodeQlStaticFieldWrittenByInstanceAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual(source.LastIndexOf("s_number", StringComparison.Ordinal), diagnostic.Location.SourceSpan.Start);
        Assert.AreEqual("s_number".Length, diagnostic.Location.SourceSpan.Length);
    }

    /// <summary>
    /// Diagnoses coalescing writes with their complete qualified field access.
    /// </summary>
    [TestMethod]
    public async Task ReportsCoalescingSharedFieldWrite()
    {
        const string Source = """
            internal sealed class Sample
            {
                internal static string s_text;
                internal string Text => Sample.s_text ??= "initialized";
            }
            """;
        Diagnostic diagnostic = Assert.ContainsSingle(await AnalyzeAsync(Source).ConfigureAwait(false));
        Assert.AreEqual(CodeQlStaticFieldWrittenByInstanceAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(Source.IndexOf("Sample.s_text", StringComparison.Ordinal), diagnostic.Location.SourceSpan.Start);
        Assert.AreEqual("Sample.s_text".Length, diagnostic.Location.SourceSpan.Length);
    }

    /// <summary>
    /// Preserves explicit static ownership, ordinary reads, and instance or distinct-type storage.
    /// </summary>
    /// <param name="source">The valid shared or instance state implementation.</param>
    [TestMethod]
    [DataRow("internal class Sample { static int s_number; static Sample() { s_number = 1; } }")]
    [DataRow("internal class Sample { static int s_number = 1; internal int Read() => s_number; }")]
    [DataRow("internal class Sample { static int s_number; internal static void Set(int value) => s_number = value; }")]
    [DataRow("internal class Sample { static int s_number; internal static int Number { set => s_number = value; } }")]
    [DataRow("internal class Sample { int _number; internal Sample(int value) { _number = value; } }")]
    [DataRow("internal class Sample { static int s_number; internal int Read() { int value = s_number; return value; } }")]
    [DataRow("internal class Sample { static readonly int[] s_numbers = new int[1]; internal void Set() { s_numbers[0] = 1; } }")]
    [DataRow("internal class Storage { internal static int s_number; } internal class Sample { internal void Set() { Storage.s_number = 1; } }")]
    [DataRow("internal class Storage { internal static int s_number; } internal class Sample : Storage { internal void Set() { s_number = 1; } }")]
    public async Task AcceptsDistinctStorageOwnership(string source)
    {
        Assert.IsEmpty(await AnalyzeAsync(source).ConfigureAwait(false));
    }

    private Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source) => CodeQlFileCompilation.AnalyzeAsync(
        source, new CodeQlStaticFieldWrittenByInstanceAnalyzer(), testContext.CancellationToken);
}
