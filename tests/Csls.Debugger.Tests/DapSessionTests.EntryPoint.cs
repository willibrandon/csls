using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies entry stopping through real managed launches and DAP streams.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Stops before the first authored statement in C#, Visual Basic, and F# entry methods.
    /// </summary>
    [TestMethod]
    [DataRow("CSharp", "cs", "internal static int Main(string[] arguments)", 1, "Debug")]
    [DataRow("VisualBasic", "vb", "Friend Function Main(arguments As String()) As Integer", 0, "Debug")]
    [DataRow("FSharp", "fs", "let mutable answer = Int32.Parse", 0, "Debug")]
    [DataRow("CSharp", "cs", "if (arguments is", 0, "Release")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StopAtEntryUsesAuthoredLanguageEntry(
        string language,
        string extension,
        string marker,
        int lineOffset,
        string configuration)
    {
        string project = $"Csls.Debugger.Fixtures.{language}";
        string program = DebuggerLanguageFixtures.GetProgramPath(project, configuration);
        string source = Path.Join(FindRepositoryRoot(), "test-assets", project, $"Program.{extension}");
        int expectedLine = FindSourceLine(await File.ReadAllLinesAsync(source, TestContext.CancellationToken)
            .ConfigureAwait(false), marker) + lineOffset;
        string signal = Path.Join(Path.GetTempPath(), $"csls-entry-{Guid.NewGuid():N}.signal");
        try
        {
            await File.WriteAllTextAsync(signal, "release", TestContext.CancellationToken).ConfigureAwait(false);
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            (int threadId, _) = await LaunchAtEntryAsync(client, program, [signal, "41", "entry-result"])
                .ConfigureAwait(false);
            (string name, string? path, int line) = await ReadSourceFrameAsync(
                client, threadId, source, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(source, path);
            Assert.AreEqual(expectedLine, line);
            Assert.Contains("main", name, StringComparison.OrdinalIgnoreCase);
            await ContinueEntryToExitAsync(client, threadId, "entry-result").ConfigureAwait(false);
        }
        finally
        {
            File.Delete(signal);
        }
    }

    /// <summary>
    /// Enters the authored async top-level method before it produces target output.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StopAtEntryUsesAsyncTopLevelSource(bool useAppHost)
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        string source = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "Program.cs");
        int expectedLine = FindSourceLine(await File.ReadAllLinesAsync(source, TestContext.CancellationToken)
            .ConfigureAwait(false), "if (args is [\"--unix-wait-status-fixture\"");
        string program = useAppHost
            ? ResolveEntryAppHost()
            : ResolveTestProcessHost();
        (int threadId, _) = await LaunchAtEntryAsync(client, program,
            ["--print-environment", "CSLS_DEBUGGER_ENTRY_VALUE"]).ConfigureAwait(false);
        (_, string? path, int line) = await ReadSourceFrameAsync(
            client, threadId, source, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(source, path);
        Assert.AreEqual(expectedLine, line);
        await ContinueEntryToExitAsync(client, threadId, "entry-result").ConfigureAwait(false);
    }

    private static string ResolveEntryAppHost() => Path.ChangeExtension(
        ResolveTestProcessHost(), OperatingSystem.IsWindows() ? ".exe" : null);

    private async Task<(int ThreadId, int ProcessId)> LaunchAtEntryAsync(
        DapTestClient client, string program, string[] arguments)
    {
        int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken,
            writeProperties: writer => writer.WriteBoolean("supportsVariablePaging", true)).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, initialize, "initialize", success: true);
        }

        int launch = await client.SendRequestAsync("launch", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("program", program);
            writer.WriteBoolean("stopAtEntry", true);
            writer.WriteStartArray("args");
            foreach (string argument in arguments)
            {
                writer.WriteStringValue(argument);
            }

            writer.WriteEndArray();
            WriteDefaultSourceFileMap(writer);
            writer.WriteStartObject("env");
            writer.WriteString("CSLS_DEBUGGER_ENTRY_VALUE", "entry-result");
            writer.WriteString("--print-environment", "entry-mutated");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(initialized.RootElement, "initialized");
        }

        int configuration = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, configuration, "configurationDone", success: true);
        }

        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, launch, "launch", success: true);
        }

        int processId;
        using (JsonDocument process = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(process.RootElement, "process");
            processId = process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32();
            Assert.IsGreaterThan(0, processId);
        }

        using JsonDocument stopped = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(stopped.RootElement, "stopped");
        JsonElement body = stopped.RootElement.GetProperty("body");
        Assert.AreEqual("entry", body.GetProperty("reason").GetString());
        Assert.IsTrue(body.GetProperty("allThreadsStopped").GetBoolean());
        int threadId = body.GetProperty("threadId").GetInt32();
        Assert.IsGreaterThan(0, threadId);
        return (threadId, processId);
    }

    private async Task ContinueEntryToExitAsync(DapTestClient client, int threadId, string expectedOutput)
    {
        int sequence = await client.SendRequestAsync("continue", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("threadId", threadId);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        var output = new System.Text.StringBuilder();
        bool responseReceived = false;
        bool continuedReceived = false;
        bool exitedReceived = false;
        bool terminatedReceived = false;
        while (!terminatedReceived)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                AssertResponse(root, sequence, "continue", success: true);
                Assert.IsFalse(responseReceived);
                responseReceived = true;
                continue;
            }

            string? eventName = root.GetProperty("event").GetString();
            switch (eventName)
            {
                case "continued":
                    Assert.IsFalse(continuedReceived);
                    continuedReceived = true;
                    break;
                case "output":
                    Assert.AreEqual("stdout", root.GetProperty("body").GetProperty("category").GetString());
                    _ = output.Append(root.GetProperty("body").GetProperty("output").GetString());
                    break;
                case "exited":
                    Assert.IsFalse(exitedReceived);
                    Assert.AreEqual(0, root.GetProperty("body").GetProperty("exitCode").GetInt32());
                    exitedReceived = true;
                    break;
                case "terminated":
                    Assert.IsTrue(exitedReceived);
                    terminatedReceived = true;
                    break;
                default:
                    Assert.Fail($"Unexpected event after entry continuation: {root.GetRawText()}");
                    break;
            }
        }

        Assert.IsTrue(responseReceived);
        Assert.IsTrue(continuedReceived);
        Assert.AreEqual(expectedOutput, output.ToString());
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }
}
