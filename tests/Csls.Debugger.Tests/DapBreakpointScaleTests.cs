using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Exercises source breakpoint replacement against compiler-produced executable statements over real DAP streams.
/// </summary>
[TestClass]
public sealed class DapBreakpointScaleTests : DapTestContext
{
    /// <summary>
    /// Retains breakpoint identities across reordered replacements and removals before stopping and inspecting the target.
    /// </summary>
    /// <param name="count">The number of distinct executable source locations installed in one session.</param>
    [TestMethod]
    [DataRow(3)]
    [DataRow(10000)]
    [TestCategory("DebuggerStress")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task SourceBreakpointReplacementPreservesIdentityAndExecution(int count)
    {
        string directory = Directory.CreateTempSubdirectory("csls-breakpoint-scale-").FullName;
        try
        {
            (string program, string source, int[] lines) = await EmitTargetAsync(directory, count).ConfigureAwait(false);
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
            (int thread, _) = await LaunchAtEntryAsync(client, program, []).ConfigureAwait(false);
            try
            {
                Dictionary<int, int> original = await SetBreakpointsAsync(client, source, lines).ConfigureAwait(false);
                Dictionary<int, int> reordered = await SetBreakpointsAsync(client, source, [.. lines.Reverse()])
                    .ConfigureAwait(false);
                AssertRetainedIdentities(original, reordered);
                int[] retainedLines = [.. lines.Where((_, index) => index % 2 == 0)];
                Dictionary<int, int> retained = await SetBreakpointsAsync(client, source, retainedLines).ConfigureAwait(false);
                AssertRetainedIdentities(original, retained);
                JsonElement entry = await ReadDeepStackPageAsync(client, thread, 0, 1).ConfigureAwait(false);
                Assert.HasCount(1, entry.GetProperty("stackFrames").EnumerateArray());

                Dictionary<int, int> anchors = await SetBreakpointsAsync(client, source, [lines[0], lines[^1]])
                    .ConfigureAwait(false);
                Assert.AreEqual(original[lines[0]], anchors[lines[0]]);
                if (retained.ContainsKey(lines[^1]))
                {
                    Assert.AreEqual(original[lines[^1]], anchors[lines[^1]]);
                }
                else
                {
                    Assert.DoesNotContain(anchors[lines[^1]], original.Values);
                }
                thread = await ContinueToBreakpointAsync(client, thread).ConfigureAwait(false);
                await AssertValueAsync(client, thread, source, lines[0], 0).ConfigureAwait(false);
                Dictionary<int, int> last = await SetBreakpointsAsync(client, source, [lines[^1]]).ConfigureAwait(false);
                Assert.AreEqual(anchors[lines[^1]], last[lines[^1]]);
                thread = await ContinueToBreakpointAsync(client, thread).ConfigureAwait(false);
                await AssertValueAsync(client, thread, source, lines[^1], count - 1).ConfigureAwait(false);
                Assert.IsEmpty(await SetBreakpointsAsync(client, source, []).ConfigureAwait(false));
                await ContinueEntryToExitAsync(client, thread, count.ToString(CultureInfo.InvariantCulture) + Environment.NewLine)
                    .ConfigureAwait(false);
            }
            catch
            {
                TestContext.WriteLine(client.ProtocolTranscript);
                TestContext.WriteLine(client.Diagnostics.ToString());
                throw;
            }
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        Assert.IsFalse(Directory.Exists(directory));
    }

    private async Task<Dictionary<int, int>> SetBreakpointsAsync(DapTestClient client, string source, int[] lines)
    {
        long started = Stopwatch.GetTimestamp();
        JsonElement breakpoints = await ReadBreakpointsAsync(client, source, lines).ConfigureAwait(false);
        var identities = new Dictionary<int, int>();
        var seen = new HashSet<int>();
        for (int index = 0; index < lines.Length; index++)
        {
            JsonElement breakpoint = breakpoints[index];
            Assert.IsTrue(breakpoint.GetProperty("verified").GetBoolean(), breakpoint.GetRawText());
            Assert.AreEqual(lines[index], breakpoint.GetProperty("line").GetInt32());
            int id = breakpoint.GetProperty("id").GetInt32();
            Assert.IsGreaterThan(0, id);
            Assert.IsTrue(seen.Add(id), $"Breakpoint {id} was returned for multiple source locations.");
            identities.Add(lines[index], id);
        }
        TestContext.WriteLine($"Set {lines.Length} source breakpoints in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms.");
        return identities;
    }

    /// <summary>
    /// Preserves independently requested columns and duplicate locations without reusing retired identifiers.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task SourceBreakpointColumnsAndDuplicatesRetainDistinctIdentities()
    {
        string directory = Directory.CreateTempSubdirectory("csls-breakpoint-identity-").FullName;
        try
        {
            (string program, string source, int[] lines) = await EmitTargetAsync(directory, 3).ConfigureAwait(false);
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
            (int thread, _) = await LaunchAtEntryAsync(client, program, []).ConfigureAwait(false);
            int first = lines[0];
            int last = lines[^1];
            JsonElement original = await ReadBreakpointsAsync(client, source, [first, first, first, last], [null, 9, null, null])
                .ConfigureAwait(false);
            int[] ids = [.. original.EnumerateArray().Select(static breakpoint => breakpoint.GetProperty("id").GetInt32())];
            Assert.HasCount(4, ids.Distinct());
            JsonElement reordered = await ReadBreakpointsAsync(client, source, [last, first, first, first], [null, null, 9, null])
                .ConfigureAwait(false);
            Assert.AreSequenceEqual([ids[3], ids[0], ids[1], ids[2]], reordered.EnumerateArray()
                .Select(static breakpoint => breakpoint.GetProperty("id").GetInt32()));
            foreach (JsonElement breakpoint in original.EnumerateArray().Concat(reordered.EnumerateArray()))
            {
                Assert.IsTrue(breakpoint.GetProperty("verified").GetBoolean(), breakpoint.GetRawText());
                Assert.AreEqual(9, breakpoint.GetProperty("column").GetInt32());
            }
            JsonElement retained = await ReadBreakpointsAsync(client, source, [first]).ConfigureAwait(false);
            Assert.AreEqual(ids[0], retained[0].GetProperty("id").GetInt32());
            JsonElement readded = await ReadBreakpointsAsync(client, source, [first, first]).ConfigureAwait(false);
            Assert.AreEqual(ids[0], readded[0].GetProperty("id").GetInt32());
            Assert.DoesNotContain(readded[1].GetProperty("id").GetInt32(), ids);
            Assert.IsEmpty((await ReadBreakpointsAsync(client, source, []).ConfigureAwait(false)).EnumerateArray());
            await ContinueEntryToExitAsync(client, thread, "3" + Environment.NewLine).ConfigureAwait(false);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        Assert.IsFalse(Directory.Exists(directory));
    }

    /// <summary>
    /// Resolves blank lines, multiline statements, and same-line columns to their compiler-authored executable spans.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task SourceBreakpointRelocationPreservesExecutableSpans()
    {
        const string text = """
            using System;
            namespace Csls.BreakpointScale;
            /// <summary>
            /// Exposes distinct executable spans within and across source lines.
            /// </summary>
            internal static class Program
            {
                /// <summary>
                /// Accumulates an independently inspected value across each authored statement.
                /// </summary>
                internal static void Main()
                {
                    int total = 0;

                    total +=
                        1;
                    total += 2; total += 3;
                    Console.WriteLine(total);
                }
            }
            """;
        string directory = Directory.CreateTempSubdirectory("csls-breakpoint-spans-").FullName;
        try
        {
            (string program, string source) = await EmitProgramAsync(directory, text).ConfigureAwait(false);
            string[] sourceLines = text.Split('\n');
            int multiline = FindSourceLine(sourceLines, "1;") - 1;
            int paired = FindSourceLine(sourceLines, "total += 2;");
            int secondColumn = sourceLines[paired - 1].IndexOf("total += 3;", StringComparison.Ordinal) + 1;
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
            (int thread, _) = await LaunchAtEntryAsync(client, program, []).ConfigureAwait(false);
            JsonElement bound = await ReadBreakpointsAsync(client, source,
                [paired, multiline + 1, multiline - 1, paired], [secondColumn, null, null, 9]).ConfigureAwait(false);
            Assert.AreSequenceEqual([paired, multiline, multiline, paired], bound.EnumerateArray()
                .Select(static breakpoint => breakpoint.GetProperty("line").GetInt32()));
            Assert.AreSequenceEqual([secondColumn, 9, 9, 9], bound.EnumerateArray()
                .Select(static breakpoint => breakpoint.GetProperty("column").GetInt32()));
            foreach (JsonElement breakpoint in bound.EnumerateArray())
            {
                Assert.IsTrue(breakpoint.GetProperty("verified").GetBoolean(), breakpoint.GetRawText());
            }
            _ = await ReadBreakpointsAsync(client, source, [paired], [secondColumn]).ConfigureAwait(false);
            thread = await ContinueToBreakpointAsync(client, thread).ConfigureAwait(false);
            await AssertValueAsync(client, thread, source, paired, 3).ConfigureAwait(false);
            _ = await ReadBreakpointsAsync(client, source, []).ConfigureAwait(false);
            await ContinueEntryToExitAsync(client, thread, "6" + Environment.NewLine).ConfigureAwait(false);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        Assert.IsFalse(Directory.Exists(directory));
    }

    /// <summary>
    /// Preserves accumulated hits while replacing another breakpoint in the same source document.
    /// </summary>
    /// <param name="hitCondition">The retained exact, threshold, or modulo hit predicate.</param>
    [TestMethod]
    [DataRow("2")]
    [DataRow(">=2")]
    [DataRow("%2")]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task SourceBreakpointReplacementPreservesAccumulatedHits(string hitCondition) =>
        ExerciseHitConditionReplacementAsync(hitCondition, hitCondition, 1);

    /// <summary>
    /// Starts the replacement hit predicate at its first hit while preserving the logical breakpoint identity.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task SourceBreakpointReplacementAppliesChangedHitCondition() =>
        ExerciseHitConditionReplacementAsync("2", "3", 6);

    private async Task ExerciseHitConditionReplacementAsync(string hitCondition, string replacement, int expectedTotal)
    {
        const string text = """
            using System;
            namespace Csls.BreakpointScale;
            /// <summary>
            /// Keeps a counted source location live while another breakpoint is replaced.
            /// </summary>
            internal static class Program
            {
                /// <summary>
                /// Accumulates known values on each iteration before printing their sum.
                /// </summary>
                internal static void Main()
                {
                    int total = 0;
                    for (int iteration = 1; iteration <= 4; iteration++)
                    {
                        int observed = iteration;
                        total += observed;
                    }
                    Console.WriteLine(total);
                }
            }
            """;
        string directory = Directory.CreateTempSubdirectory("csls-breakpoint-hits-").FullName;
        try
        {
            (string program, string source) = await EmitProgramAsync(directory, text).ConfigureAwait(false);
            string[] sourceLines = text.Split('\n');
            int marker = FindSourceLine(sourceLines, "int observed =");
            int counted = FindSourceLine(sourceLines, "total += observed;");
            int final = FindSourceLine(sourceLines, "Console.WriteLine(total);");
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
            (int thread, _) = await LaunchAtEntryAsync(client, program, []).ConfigureAwait(false);
            JsonElement original = await ReadBreakpointsAsync(client, source, [marker, counted],
                conditions: ["iteration == 2", "observed >= 1"], hitConditions: [null, hitCondition]).ConfigureAwait(false);
            foreach (JsonElement breakpoint in original.EnumerateArray())
            {
                Assert.IsTrue(breakpoint.GetProperty("verified").GetBoolean(), breakpoint.GetRawText());
            }
            thread = await ContinueToBreakpointAsync(client, thread).ConfigureAwait(false);
            await AssertValueAsync(client, thread, source, marker, 1).ConfigureAwait(false);
            JsonElement replaced = await ReadBreakpointsAsync(client, source, [counted, final],
                conditions: ["observed >= 1", null], hitConditions: [replacement, null]).ConfigureAwait(false);
            Assert.AreEqual(original[1].GetProperty("id").GetInt32(), replaced[0].GetProperty("id").GetInt32());
            thread = await ContinueToBreakpointAsync(client, thread).ConfigureAwait(false);
            await AssertValueAsync(client, thread, source, counted, expectedTotal).ConfigureAwait(false);
            _ = await ReadBreakpointsAsync(client, source, [final]).ConfigureAwait(false);
            thread = await ContinueToBreakpointAsync(client, thread).ConfigureAwait(false);
            await AssertValueAsync(client, thread, source, final, 10).ConfigureAwait(false);
            _ = await ReadBreakpointsAsync(client, source, []).ConfigureAwait(false);
            await ContinueEntryToExitAsync(client, thread, "10" + Environment.NewLine).ConfigureAwait(false);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        Assert.IsFalse(Directory.Exists(directory));
    }

    /// <summary>
    /// Deactivates previously bound locations when refreshed source or symbols invalidate the replacement.
    /// </summary>
    /// <param name="changeSource">Whether to edit source text or retire its symbol file.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task SourceBreakpointReplacementRetiresInvalidatedBindings(bool changeSource)
    {
        string directory = Directory.CreateTempSubdirectory("csls-breakpoint-refresh-").FullName;
        try
        {
            (string program, string source, int[] lines) = await EmitTargetAsync(directory, 3).ConfigureAwait(false);
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
            (int thread, _) = await LaunchAtEntryAsync(client, program, []).ConfigureAwait(false);
            Dictionary<int, int> original = await SetBreakpointsAsync(client, source, lines).ConfigureAwait(false);
            if (changeSource)
            {
                await File.AppendAllTextAsync(source, "\n// Changed after binding.\n", TestContext.CancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                File.Move(Path.ChangeExtension(program, ".pdb"), Path.Join(directory, "retired.pdb"));
            }
            JsonElement replaced = await ReadBreakpointsAsync(client, source, lines).ConfigureAwait(false);
            for (int index = 0; index < lines.Length; index++)
            {
                Assert.AreEqual(original[lines[index]], replaced[index].GetProperty("id").GetInt32());
                Assert.IsFalse(replaced[index].GetProperty("verified").GetBoolean(), replaced[index].GetRawText());
                string? message = replaced[index].GetProperty("message").GetString();
                Assert.IsNotNull(message);
                Assert.Contains(changeSource ? "source file differs" : "pending", message);
            }
            await ContinueEntryToExitAsync(client, thread, "3" + Environment.NewLine).ConfigureAwait(false);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        Assert.IsFalse(Directory.Exists(directory));
    }

    private async Task<JsonElement> ReadBreakpointsAsync(DapTestClient client, string source, int[] lines,
        int?[]? columns = null, string?[]? conditions = null, string?[]? hitConditions = null)
    {
        int sequence = await client.SendRequestAsync("setBreakpoints", writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartObject("source");
            writer.WriteString("path", source);
            writer.WriteEndObject();
            writer.WriteStartArray("breakpoints");
            for (int index = 0; index < lines.Length; index++)
            {
                writer.WriteStartObject();
                writer.WriteNumber("line", lines[index]);
                if (columns?[index] is int column)
                {
                    writer.WriteNumber("column", column);
                }
                if (conditions?[index] is string condition)
                {
                    writer.WriteString("condition", condition);
                }
                if (hitConditions?[index] is string hitCondition)
                {
                    writer.WriteString("hitCondition", hitCondition);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "setBreakpoints", success: true);
        JsonElement breakpoints = response.RootElement.GetProperty("body").GetProperty("breakpoints");
        Assert.AreEqual(lines.Length, breakpoints.GetArrayLength());
        return breakpoints.Clone();
    }

    private static void AssertRetainedIdentities(Dictionary<int, int> original, Dictionary<int, int> replacement)
    {
        foreach ((int line, int id) in replacement)
        {
            Assert.AreEqual(original[line], id, $"Replacing source breakpoints changed the identity at line {line}.");
        }
    }

    private async Task<int> ContinueToBreakpointAsync(DapTestClient client, int thread)
    {
        int sequence = await client.SendRequestAsync("continue", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("threadId", thread);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        bool responded = false;
        bool continued = false;
        int? stoppedThread = null;
        while (!responded || !continued || stoppedThread is null)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                AssertResponse(root, sequence, "continue", success: true);
                Assert.IsFalse(responded);
                responded = true;
            }
            else if (root.GetProperty("event").GetString() == "continued")
            {
                Assert.IsFalse(continued);
                continued = true;
            }
            else
            {
                AssertEvent(root, "stopped");
                Assert.IsNull(stoppedThread);
                JsonElement body = root.GetProperty("body");
                Assert.AreEqual("breakpoint", body.GetProperty("reason").GetString());
                Assert.IsTrue(body.GetProperty("allThreadsStopped").GetBoolean());
                stoppedThread = body.GetProperty("threadId").GetInt32();
            }
        }
        return stoppedThread.Value;
    }

    private async Task AssertValueAsync(DapTestClient client, int thread, string source, int line, int expected)
    {
        JsonElement stack = await ReadDeepStackPageAsync(client, thread, 0, 1).ConfigureAwait(false);
        JsonElement frame = Assert.ContainsSingle(stack.GetProperty("stackFrames").EnumerateArray());
        Assert.AreEqual(line, frame.GetProperty("line").GetInt32());
        Assert.IsTrue(DebuggerTestPath.AreEquivalent(source, frame.GetProperty("source").GetProperty("path").GetString()));
        JsonElement value = await ReadEvaluationAsync(client, frame.GetProperty("id").GetInt32(), "total", true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(expected.ToString(CultureInfo.InvariantCulture), value.GetProperty("result").GetString());
        Assert.AreEqual("int", value.GetProperty("type").GetString());
    }

    private async Task<(string Program, string Source, int[] Lines)> EmitTargetAsync(string directory, int count)
    {
        var sourceLines = new List<string>
        {
            "using System;", "namespace Csls.BreakpointScale;",
            "/// <summary>", "/// Accumulates a value across independently executable source locations.", "/// </summary>",
            "internal static class Program", "{",
            "    /// <summary>", "    /// Prints the result after each source breakpoint location has executed.", "    /// </summary>",
            "    internal static void Main()", "    {", "        int total = 0;"
        };
        int[] lines = new int[count];
        for (int index = 0; index < count; index++)
        {
            sourceLines.Add("        total += 1;");
            lines[index] = sourceLines.Count;
        }
        sourceLines.AddRange(["        Console.WriteLine(total);", "    }", "}"]);
        string source = string.Join('\n', sourceLines);
        (string programPath, string sourcePath) = await EmitProgramAsync(directory, source).ConfigureAwait(false);
        return (programPath, sourcePath, lines);
    }

    private async Task<(string Program, string Source)> EmitProgramAsync(string directory, string source)
    {
        long started = Stopwatch.GetTimestamp();
        TestContext.WriteLine($"Compiling breakpoint fixture in {directory}.");
        string sourcePath = Path.Join(directory, "Program.cs");
        string programPath = Path.Join(directory, "Csls.BreakpointScale.dll");
        await File.WriteAllTextAsync(sourcePath, source, Encoding.UTF8, TestContext.CancellationToken).ConfigureAwait(false);
        SyntaxTree syntax = CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8, SourceHashAlgorithm.Sha256),
            new CSharpParseOptions(LanguageVersion.CSharp14), sourcePath, TestContext.CancellationToken);
        string platformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException("The trusted platform assembly list is unavailable.");
        var compilation = CSharpCompilation.Create("Csls.BreakpointScale", [syntax],
            platformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(static path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Debug,
                deterministic: true, nullableContextOptions: NullableContextOptions.Enable,
                generalDiagnosticOption: ReportDiagnostic.Error));
        TestContext.WriteLine($"Created breakpoint compilation in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms.");
        using (var pe = new FileStream(programPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var pdb = new FileStream(Path.ChangeExtension(programPath, ".pdb"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            EmitResult emitted = compilation.Emit(pe, pdb,
                options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
                cancellationToken: TestContext.CancellationToken);
            Assert.IsTrue(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        }
        File.Copy(Path.ChangeExtension(ResolveTestProcessHost(), ".runtimeconfig.json"),
            Path.ChangeExtension(programPath, ".runtimeconfig.json"));
        TestContext.WriteLine($"Emitted breakpoint fixture in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms.");
        return (programPath, sourcePath);
    }
}
