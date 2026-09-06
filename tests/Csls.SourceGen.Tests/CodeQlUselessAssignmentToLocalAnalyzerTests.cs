using Microsoft.CodeAnalysis;
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
    /// Gets the cancellation context for the file-backed analyzer fixtures.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Rejects a local update in finally after the return value has already been evaluated.
    /// </summary>
    /// <param name="update">The unread local update.</param>
    /// <param name="asynchronous">Whether the fixture suspends before returning.</param>
    [TestMethod]
    [DataRow("answer++", false)]
    [DataRow("++answer", false)]
    [DataRow("answer--", false)]
    [DataRow("--answer", false)]
    [DataRow("answer++", true)]
    [DataRow("++answer", true)]
    [DataRow("answer--", true)]
    [DataRow("--answer", true)]
    public async Task ReportsUnreadFinallyUpdate(string update, bool asynchronous)
    {
        ArgumentNullException.ThrowIfNull(update);
        string returnType = asynchronous ? "async System.Threading.Tasks.Task<int>" : "int";
        string suspension = asynchronous ? "await System.Threading.Tasks.Task.Yield();" : string.Empty;
        string source = $$"""
            internal static class Projection
            {
                internal static {{returnType}} Read(int input)
                {
                    int answer = input;
                    {{suspension}}
                    try { return answer + 1; }
                    finally { {{update}}; }
                }
            }
            """;

        Diagnostic diagnostic = Assert.ContainsSingle(await AnalyzeAsync(source).ConfigureAwait(false));
        Assert.AreEqual(CodeQlUselessAssignmentToLocalAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual(source.IndexOf(update, StringComparison.Ordinal), diagnostic.Location.SourceSpan.Start);
        Assert.AreEqual(update.Length, diagnostic.Location.SourceSpan.Length);
        Assert.Contains("answer", diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Preserves updates observed by subsequent reads, closures, references, or expression consumers.
    /// </summary>
    /// <param name="body">The method body that observes an update.</param>
    [TestMethod]
    [DataRow("int answer = input; answer++; return answer;")]
    [DataRow("int answer = input; --answer; return answer;")]
    [DataRow("int answer = input; try { System.Console.WriteLine(answer); } finally { answer++; } return answer;")]
    [DataRow("int answer = input; try { return answer; } finally { answer++; System.Console.WriteLine(answer); }")]
    [DataRow("int answer = input; System.Func<int> read = () => answer; answer++; return read();")]
    [DataRow("ref int answer = ref input; answer++; return input;")]
    [DataRow("int answer = input; ref int alias = ref answer; answer++; return alias;")]
    [DataRow("int answer = input; ref int alias = ref (answer); answer++; return alias;")]
    [DataRow("int answer = input; return ++answer;")]
    [DataRow("int answer = input; return answer++;")]
    [DataRow("int answer = 0; for (; answer < input; answer++) { System.Console.WriteLine(answer); } return answer;")]
    public async Task AcceptsObservedLocalUpdates(string body)
    {
        string source = $$"""
            internal static class Projection
            {
                internal static int Read(int input) { {{body}} }
            }
            """;
        Assert.IsEmpty(await AnalyzeAsync(source).ConfigureAwait(false));
    }

    /// <summary>
    /// Preserves user-defined operators and updates to externally observable storage.
    /// </summary>
    [TestMethod]
    public async Task AcceptsEffectfulUpdates()
    {
        const string Source = """
            internal sealed class Counter
            {
                internal int Value { get; set; }
                public static Counter operator ++(Counter value) { value.Value++; return value; }
            }
            internal static class Projection
            {
                internal static void Read(Counter input, int[] values, ref int reference)
                {
                    Counter counter = input;
                    counter++;
                    input.Value++;
                    values[0]++;
                    reference++;
                }
            }
            """;
        Assert.IsEmpty(await AnalyzeAsync(Source).ConfigureAwait(false));
    }

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

    /// <summary>
    /// Verifies an unused foreach iteration value is rejected.
    /// </summary>
    [TestMethod]
    public async Task ReportsUnreadForEachIterationValue()
    {
        const string Source = """
            internal static class Projection
            {
                internal static bool Any(int[] values)
                {
                    foreach (int value in values)
                    {
                        return true;
                    }

                    return false;
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
            "value",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Verifies an observed foreach iteration value remains accepted.
    /// </summary>
    [TestMethod]
    public async Task AcceptsObservedForEachIterationValue()
    {
        const string Source = """
            internal static class Projection
            {
                internal static int Sum(int[] values)
                {
                    int result = 0;
                    foreach (int value in values)
                    {
                        result += value;
                    }

                    return result;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source)
            .ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    private Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source) => CodeQlFileCompilation.AnalyzeAsync(
        source, new CodeQlUselessAssignmentToLocalAnalyzer(), TestContext.CancellationToken);
}
