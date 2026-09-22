using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies target-exit behavior while a managed source step is active.
/// </summary>
[TestClass]
public sealed class DapStepExitTests : DapTestContext
{
    /// <summary>
    /// Completes the step response and emits one terminal sequence when the target calls Environment.Exit.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task EnvironmentExitDuringStepCompletesAndTerminates()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
        string source = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "DebuggerStepFixture.cs");
        int line = FindSourceLine(await File.ReadAllLinesAsync(source, TestContext.CancellationToken)
            .ConfigureAwait(false), "Environment.Exit(37);");

        int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, initialize, "initialize", success: true);
        }

        int launch = await client.SendRequestAsync("launch", writer => WriteLaunchArguments(writer,
            ResolveTestProcessHost(), ["--debugger-exit-on-step-fixture"], wait: true, noDebug: false),
            TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertEvent(initialized.RootElement, "initialized");
        }

        int breakpoint = await client.SendRequestAsync("setBreakpoints",
            writer => WriteSourceBreakpointArguments(writer, source, line), TestContext.CancellationToken)
            .ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, breakpoint, "setBreakpoints", success: true);
        }

        int configuration = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        int threadId = await ReadInitialBreakpointStopAsync(client, configuration, launch,
            TestContext.CancellationToken, TestContext).ConfigureAwait(false);
        (string name, string? path, int stoppedLine) = await ReadSourceFrameAsync(client, threadId, source,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("Csls.TestProcessHost.DebuggerStepFixture.ExitOnStep", name);
        Assert.IsTrue(DebuggerTestPath.AreEquivalent(source, path));
        Assert.AreEqual(line, stoppedLine);
        int targetId = Assert.IsInstanceOfType<int>(client.TargetProcessId);
        using var target = Process.GetProcessById(targetId);
        _ = target.SafeHandle;

        int step = await client.SendRequestAsync("next", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("threadId", threadId);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        bool continuedReceived = false;
        bool responseReceived = false;
        bool exitedReceived = false;
        bool terminatedReceived = false;
        while (!responseReceived || !terminatedReceived)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                Assert.IsFalse(responseReceived);
                AssertResponse(root, step, "next", success: true);
                responseReceived = true;
                continue;
            }

            switch (root.GetProperty("event").GetString())
            {
                case "continued":
                    Assert.IsFalse(continuedReceived);
                    Assert.IsFalse(exitedReceived);
                    continuedReceived = true;
                    break;
                case "exited":
                    Assert.IsFalse(exitedReceived);
                    Assert.IsTrue(continuedReceived);
                    Assert.AreEqual(37, root.GetProperty("body").GetProperty("exitCode").GetInt32());
                    exitedReceived = true;
                    break;
                case "terminated":
                    Assert.IsFalse(terminatedReceived);
                    Assert.IsTrue(exitedReceived);
                    terminatedReceived = true;
                    break;
                default:
                    Assert.Fail($"Unexpected event while stepping through exit: {root.GetRawText()}");
                    break;
            }
        }

        Assert.IsTrue(continuedReceived);
        Assert.IsTrue(exitedReceived);
        await target.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsTrue(target.HasExited);
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(async () =>
        {
            using JsonDocument extra = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);
        Assert.IsEmpty(client.Diagnostics.ToString());
    }
}
