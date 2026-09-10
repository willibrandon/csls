using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using System.Globalization;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies repeated string accumulation diagnostics on actual file-backed compilations.
/// </summary>
/// <param name="testContext">The running test's cancellation context.</param>
[TestClass]
public sealed class CodeQlStringConcatenationInLoopAnalyzerTests(TestContext testContext)
{
    /// <summary>
    /// Reports compound and self-referential addition for variables retained between loop iterations.
    /// </summary>
    /// <param name="body">The source operation placed in the real compilation.</param>
    /// <param name="expression">The exact assignment that requires a string builder.</param>
    /// <param name="variable">The retained string's name.</param>
    [TestMethod]
    [DataRow("string output = string.Empty; while (output.Length < 7) { output += Read(); }", "output += Read()", "output")]
    [DataRow("string output = string.Empty; do { output += Read(); } while (output.Length < 7);", "output += Read()", "output")]
    [DataRow("string output = string.Empty; for (int i = 0; i < 7; i++) { output = output + Read(); }", "output = output + Read()", "output")]
    [DataRow("string output = string.Empty; foreach (string item in items) { output = item + output; }", "output = item + output", "output")]
    [DataRow("string output = string.Empty; foreach (string item in items) { output = (item + output) + item; }", "output = (item + output) + item", "output")]
    [DataRow("foreach (string item in items) { seed += item; }", "seed += item", "seed")]
    [DataRow("foreach (string item in items) { _output += item; }", "_output += item", "_output")]
    [DataRow("foreach (string item in items) { string output = item; for (int i = 0; i < 7; i++) { output += Read(); } }", "output += Read()", "output")]
    [DataRow("string output = string.Empty; for (int i = 0; i < 7; i++, output += Read()) { }", "output += Read()", "output")]
    [DataRow("System.Action read = () => { string output = string.Empty; while (output.Length < 7) { output += Read(); } }; read();", "output += Read()", "output")]
    [DataRow("void ReadAll() { string output = string.Empty; while (output.Length < 7) { output += Read(); } } ReadAll();", "output += Read()", "output")]
    public async Task ReportsRetainedStringAccumulation(string body, string expression, string variable)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(body).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlStringConcatenationInLoopAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual(
            $"Use StringBuilder instead of accumulating string '{variable}' with concatenation in a loop",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.IsNotNull(diagnostic.Location.SourceTree);
        Assert.AreEqual(expression, (await diagnostic.Location.SourceTree.GetTextAsync(testContext.CancellationToken)
            .ConfigureAwait(false)).ToString(diagnostic.Location.SourceSpan));
    }

    /// <summary>
    /// Accepts per-iteration strings, other operations, and assignments inside separate function bodies.
    /// </summary>
    /// <param name="body">The source operation that does not accumulate strings across iterations.</param>
    [TestMethod]
    [DataRow("string output = string.Empty; output += Read();")]
    [DataRow("int count = 0; foreach (string item in items) { count += item.Length; }")]
    [DataRow("var output = new System.Text.StringBuilder(); foreach (string item in items) { output.Append(item); }")]
    [DataRow("foreach (string item in items) { string output = item; output += Read(); }")]
    [DataRow("foreach (string item in items) { string output = item; output = output + Read(); }")]
    [DataRow("for (string output = string.Empty; output.Length < 7; output += Read()) { }")]
    [DataRow("string output = string.Empty; foreach (string item in items) { output = item + Read(); }")]
    [DataRow("string output = string.Empty; foreach (string item in items) { output = Read(output) + item; }")]
    [DataRow("string output = string.Empty; foreach (string item in items) { System.Action read = () => output += item; }")]
    [DataRow("string output = string.Empty; foreach (string item in items) { void ReadOne() { output += item; } }")]
    [DataRow("foreach (string item in items) { Output += item; }")]
    [DataRow("string[] output = [string.Empty]; foreach (string item in items) { output[0] += item; }")]
    public async Task AcceptsOperationsWithoutRetainedStringAccumulation(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(body).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Reports the asynchronous protocol-output accumulation that triggered remote analysis.
    /// </summary>
    [TestMethod]
    public async Task ReportsAwaitedOutputAccumulation()
    {
        const string Source = """
            internal static class Reader
            {
                internal static async System.Threading.Tasks.Task<string> ReadAsync(System.IO.StreamReader reader)
                {
                    string output = string.Empty;
                    while (output.Length < 7)
                    {
                        output += await reader.ReadLineAsync();
                    }
                    return output;
                }
            }
            """;
        ImmutableArray<Diagnostic> diagnostics = await CodeQlFileCompilation.AnalyzeAsync(
            Source, new CodeQlStringConcatenationInLoopAnalyzer(), testContext.CancellationToken).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlStringConcatenationInLoopAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.IsNotNull(diagnostic.Location.SourceTree);
        Assert.AreEqual("output += await reader.ReadLineAsync()",
            (await diagnostic.Location.SourceTree.GetTextAsync(testContext.CancellationToken).ConfigureAwait(false))
                .ToString(diagnostic.Location.SourceSpan));
    }

    private Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string body) => CodeQlFileCompilation.AnalyzeAsync(
        $$"""
        internal sealed class Accumulator
        {
            private string _output = string.Empty;
            private string Output { get; set; } = string.Empty;
            private static string Read(string value = "value") => value;
            internal void Accumulate(string[] items, string seed)
            {
                {{body}}
            }
        }
        """, new CodeQlStringConcatenationInLoopAnalyzer(), testContext.CancellationToken);
}
