using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies entry-stop lifetime and breakpoint coexistence across real target restarts.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Rearms entry stopping or applies a replacement policy while retaining user breakpoints.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StopAtEntryRestartPreservesUserBreakpoints(bool retainEntryStop)
    {
        string program = DebuggerLanguageFixtures.GetProgramPath("Csls.Debugger.Fixtures.CSharp", "Debug");
        string source = Path.Join(FindRepositoryRoot(), "test-assets", "Csls.Debugger.Fixtures.CSharp", "Program.cs");
        string[] sourceLines = await File.ReadAllLinesAsync(source, TestContext.CancellationToken)
            .ConfigureAwait(false);
        int breakpointLine = FindSourceLine(sourceLines, "answer++;");
        int expectedEntryLine = FindSourceLine(sourceLines, "internal static int Main(string[] arguments)") + 1;
        string signal = Path.Join(Path.GetTempPath(), $"csls-entry-restart-{Guid.NewGuid():N}.signal");
        try
        {
            await File.WriteAllTextAsync(signal, "release", TestContext.CancellationToken).ConfigureAwait(false);
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            (_, int originalProcessId) = await LaunchAtEntryAsync(client, program, [signal, "41", "entry-result"])
                .ConfigureAwait(false);
            int setSequence = await client.SendRequestAsync("setBreakpoints",
                writer => WriteSourceBreakpointArguments(writer, source, breakpointLine),
                TestContext.CancellationToken).ConfigureAwait(false);
            int breakpointId;
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, setSequence, "setBreakpoints", success: true);
                JsonElement breakpoint = Assert.ContainsSingle(response.RootElement.GetProperty("body")
                    .GetProperty("breakpoints").EnumerateArray());
                Assert.IsTrue(breakpoint.GetProperty("verified").GetBoolean());
                Assert.AreEqual(breakpointLine, breakpoint.GetProperty("line").GetInt32());
                breakpointId = breakpoint.GetProperty("id").GetInt32();
            }

            int restartSequence = await client.SendRequestAsync("restart", writer =>
            {
                writer.WriteStartObject();
                if (!retainEntryStop)
                {
                    writer.WritePropertyName("arguments");
                    WriteLaunchArguments(writer, program, [signal, "41", "entry-result"],
                        wait: true, noDebug: false, stopAtEntry: false);
                }

                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            int threadId = await ReadEntryRestartAsync(client, restartSequence, originalProcessId,
                breakpointId, retainEntryStop ? "entry" : "breakpoint").ConfigureAwait(false);
            await AssertProcessExitedAsync(originalProcessId, TestContext.CancellationToken).ConfigureAwait(false);
            if (retainEntryStop)
            {
                (_, string? entryPath, int entryLine) = await ReadSourceFrameAsync(
                    client, threadId, source, TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(source, entryPath);
                Assert.AreEqual(expectedEntryLine, entryLine);
                threadId = await ContinueEntryToUserBreakpointAsync(client).ConfigureAwait(false);
            }

            (_, string? path, int line) = await ReadSourceFrameAsync(
                client, threadId, source, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(source, path);
            Assert.AreEqual(breakpointLine, line);
            await ContinueEntryToExitAsync(client, threadId, "entry-result").ConfigureAwait(false);
        }
        finally
        {
            File.Delete(signal);
        }
    }

    private async Task<int> ReadEntryRestartAsync(DapTestClient client, int sequence,
        int originalProcessId, int breakpointId, string expectedReason)
    {
        bool exited = false;
        bool responded = false;
        bool started = false;
        bool rebound = false;
        while (true)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                Assert.IsTrue(exited);
                Assert.IsFalse(responded);
                AssertResponse(root, sequence, "restart", success: true);
                responded = true;
                continue;
            }

            JsonElement body = root.GetProperty("body");
            switch (root.GetProperty("event").GetString())
            {
                case "exited":
                    Assert.IsFalse(exited);
                    exited = true;
                    break;
                case "process":
                    Assert.IsTrue(responded);
                    Assert.IsFalse(started);
                    int processId = body.GetProperty("systemProcessId").GetInt32();
                    Assert.IsGreaterThan(0, processId);
                    Assert.AreNotEqual(originalProcessId, processId);
                    started = true;
                    break;
                case "breakpoint":
                    JsonElement breakpoint = body.GetProperty("breakpoint");
                    Assert.AreEqual(breakpointId, breakpoint.GetProperty("id").GetInt32());
                    rebound |= breakpoint.GetProperty("verified").GetBoolean();
                    break;
                case "stopped":
                    Assert.IsTrue(started);
                    Assert.IsTrue(rebound);
                    Assert.AreEqual(expectedReason, body.GetProperty("reason").GetString());
                    return body.GetProperty("threadId").GetInt32();
                default:
                    Assert.Fail($"Unexpected restart event: {root.GetRawText()}");
                    break;
            }
        }
    }

    private async Task<int> ContinueEntryToUserBreakpointAsync(DapTestClient client)
    {
        int sequence = await client.SendRequestAsync("continue", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        bool responded = false;
        bool continued = false;
        while (true)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                Assert.IsFalse(responded);
                AssertResponse(root, sequence, "continue", success: true);
                responded = true;
                continue;
            }

            if (root.GetProperty("event").GetString() == "continued")
            {
                Assert.IsFalse(continued);
                continued = true;
                continue;
            }

            AssertEvent(root, "stopped");
            Assert.IsTrue(responded);
            Assert.IsTrue(continued);
            Assert.AreEqual("breakpoint", root.GetProperty("body").GetProperty("reason").GetString());
            return root.GetProperty("body").GetProperty("threadId").GetInt32();
        }
    }
}
