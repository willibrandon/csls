using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies runtime-bound managed function breakpoints.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Resolves a pending fully qualified function breakpoint and stops at method entry.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedFunctionBreakpointBindsAndStopsAtMethodEntry()
    {
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-function-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
            _ = await client.SendRequestAsync(
                "initialize",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialize = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            int launchSequence = await client.SendRequestAsync(
                "launch",
                writer => WriteLaunchArguments(
                    writer,
                    ResolveTestProcessHost(),
                    ["--debugger-fixture", waitPath],
                    wait: true,
                    noDebug: false),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialized = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(initialized.RootElement, "initialized");

            int breakpointsSequence = await client.SendRequestAsync(
                "setFunctionBreakpoints",
                WriteFunctionBreakpointArguments,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument pending = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                pending.RootElement,
                breakpointsSequence,
                "setFunctionBreakpoints",
                success: true);
            JsonElement pendingBreakpoint = pending.RootElement
                .GetProperty("body")
                .GetProperty("breakpoints")[0];
            Assert.IsFalse(pendingBreakpoint.GetProperty("verified").GetBoolean());
            int breakpointId = pendingBreakpoint.GetProperty("id").GetInt32();

            int configurationSequence = await client.SendRequestAsync(
                "configurationDone",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            int threadId = await ReadFunctionBreakpointStopAsync(
                client,
                configurationSequence,
                launchSequence,
                breakpointId).ConfigureAwait(false);
            await AssertFunctionBreakpointFrameAsync(client, threadId).ConfigureAwait(false);
            await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                await client.WaitForExitAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private async Task<int> ReadFunctionBreakpointStopAsync(
        DapTestClient client,
        int configurationSequence,
        int launchSequence,
        int breakpointId)
    {
        bool configurationReceived = false;
        bool launchReceived = false;
        bool processReceived = false;
        bool breakpointChanged = false;
        int? stoppedThreadId = null;
        while (!configurationReceived || !launchReceived || !processReceived ||
            !breakpointChanged || stoppedThreadId is null)
        {
            using JsonDocument message = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                int requestSequence = root.GetProperty("request_seq").GetInt32();
                if (requestSequence == configurationSequence)
                {
                    AssertResponse(
                        root,
                        configurationSequence,
                        "configurationDone",
                        success: true);
                    configurationReceived = true;
                }
                else if (requestSequence == launchSequence)
                {
                    AssertResponse(root, launchSequence, "launch", success: true);
                    launchReceived = true;
                }

                continue;
            }

            string? eventName = root.GetProperty("event").GetString();
            if (eventName == "process")
            {
                Assert.IsGreaterThan(
                    0,
                    root.GetProperty("body").GetProperty("systemProcessId").GetInt32());
                processReceived = true;
            }
            else if (eventName == "breakpoint")
            {
                JsonElement breakpoint = root.GetProperty("body").GetProperty("breakpoint");
                Assert.AreEqual(breakpointId, breakpoint.GetProperty("id").GetInt32());
                Assert.IsTrue(breakpoint.GetProperty("verified").GetBoolean());
                breakpointChanged = true;
            }
            else if (eventName == "stopped")
            {
                JsonElement body = root.GetProperty("body");
                Assert.AreEqual("function breakpoint", body.GetProperty("reason").GetString());
                Assert.IsTrue(body.GetProperty("allThreadsStopped").GetBoolean());
                stoppedThreadId = body.GetProperty("threadId").GetInt32();
            }
        }

        return stoppedThreadId.Value;
    }

    private async Task AssertFunctionBreakpointFrameAsync(DapTestClient client, int threadId)
    {
        int sequence = await client.SendRequestAsync(
            "stackTrace",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("threadId", threadId);
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument stack = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(stack.RootElement, sequence, "stackTrace", success: true);
        string[] frameNames = [.. stack.RootElement
            .GetProperty("body")
            .GetProperty("stackFrames")
            .EnumerateArray()
            .Select(static frame => frame.GetProperty("name").GetString()!)];
        Assert.Contains(
            "Csls.TestProcessHost.DebuggerFixture.WaitForSignal",
            frameNames);
    }

    private static void WriteFunctionBreakpointArguments(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("breakpoints");
        writer.WriteStartObject();
        writer.WriteString("name", "Csls.TestProcessHost.DebuggerFixture.WaitForSignal");
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
