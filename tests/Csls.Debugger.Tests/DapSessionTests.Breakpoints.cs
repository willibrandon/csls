using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies runtime-bound managed source breakpoints.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Resolves a pending Portable PDB source breakpoint and stops on its runtime callback.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedSourceBreakpointBindsAndStopsAtRequestedStatement()
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourcePath = Path.Join(
            repositoryRoot,
            "tests",
            "Csls.TestProcessHost",
            "DebuggerFixture.cs");
        string[] sourceLines = await File.ReadAllLinesAsync(
            sourcePath,
            TestContext.CancellationToken).ConfigureAwait(false);
        int breakpointLine = sourceLines
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(static candidate => candidate.Line.Contains(
                "int localNumber = number + 1;",
                StringComparison.Ordinal))
            .Number;
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-breakpoint-{Guid.NewGuid():N}.signal");
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

            int setBreakpointsSequence = await client.SendRequestAsync(
                "setBreakpoints",
                writer => WriteSourceBreakpointArguments(writer, sourcePath, breakpointLine),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument pendingBreakpoints = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                pendingBreakpoints.RootElement,
                setBreakpointsSequence,
                "setBreakpoints",
                success: true);
            JsonElement pendingBreakpoint = pendingBreakpoints.RootElement
                .GetProperty("body")
                .GetProperty("breakpoints")[0];
            Assert.IsFalse(pendingBreakpoint.GetProperty("verified").GetBoolean());
            Assert.AreEqual(breakpointLine, pendingBreakpoint.GetProperty("line").GetInt32());
            int breakpointId = pendingBreakpoint.GetProperty("id").GetInt32();

            int configurationSequence = await client.SendRequestAsync(
                "configurationDone",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
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
                    Assert.AreEqual(breakpointLine, breakpoint.GetProperty("line").GetInt32());
                    breakpointChanged = true;
                }
                else if (eventName == "stopped")
                {
                    JsonElement body = root.GetProperty("body");
                    Assert.AreEqual("breakpoint", body.GetProperty("reason").GetString());
                    Assert.IsTrue(body.GetProperty("allThreadsStopped").GetBoolean());
                    stoppedThreadId = body.GetProperty("threadId").GetInt32();
                }
            }

            int stackSequence = await client.SendRequestAsync(
                "stackTrace",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("threadId", stoppedThreadId.Value);
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument stack = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(stack.RootElement, stackSequence, "stackTrace", success: true);
            JsonElement[] frames = [.. stack.RootElement
                .GetProperty("body")
                .GetProperty("stackFrames")
                .EnumerateArray()];
            JsonElement breakpointFrame = frames.First(frame => string.Equals(
                frame.GetProperty("name").GetString(),
                "Csls.TestProcessHost.DebuggerFixture.WaitForSignal",
                StringComparison.Ordinal));
            Assert.AreEqual(sourcePath, breakpointFrame.GetProperty("source").GetProperty("path").GetString());
            Assert.AreEqual(breakpointLine, breakpointFrame.GetProperty("line").GetInt32());

            await File.WriteAllTextAsync(
                waitPath,
                string.Empty,
                TestContext.CancellationToken).ConfigureAwait(false);
            int continueSequence = await client.SendRequestAsync(
                "continue",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            bool continueReceived = false;
            bool continuedReceived = false;
            bool exitedReceived = false;
            bool terminatedReceived = false;
            while (!continueReceived || !continuedReceived || !exitedReceived || !terminatedReceived)
            {
                using JsonDocument message = await client
                    .ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                JsonElement root = message.RootElement;
                if (root.GetProperty("type").GetString() == "response" &&
                    root.GetProperty("request_seq").GetInt32() == continueSequence)
                {
                    AssertResponse(root, continueSequence, "continue", success: true);
                    continueReceived = true;
                    continue;
                }

                string? eventName = root.GetProperty("event").GetString();
                continuedReceived |= eventName == "continued";
                if (eventName == "exited")
                {
                    Assert.AreEqual(0, root.GetProperty("body").GetProperty("exitCode").GetInt32());
                    exitedReceived = true;
                }

                terminatedReceived |= eventName == "terminated";
            }

            Assert.AreEqual(
                0,
                await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

}
