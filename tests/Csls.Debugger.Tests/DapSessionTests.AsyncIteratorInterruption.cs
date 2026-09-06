using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies retirement of async-consumer stepping when a user breakpoint interrupts execution.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Cancels consumer breakpoints when user code interrupts an in-flight iterator yield step.
    /// </summary>
    /// <param name="configuration">The compiler optimization configuration.</param>
    /// <param name="synchronousYield">Whether to interrupt the physical caller after a synchronous yield.</param>
    [TestMethod]
    [DataRow("Debug", false)]
    [DataRow("Release", false)]
    [DataRow("Debug", true)]
    [DataRow("Release", true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task BreakpointInterruptsAsyncIteratorConsumerStep(string configuration, bool synchronousYield)
    {
        string sourcePath = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "DebuggerAsyncIteratorStepFixture.cs");
        string[] lines = await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken).ConfigureAwait(false);
        int awaitLine = FindSourceLine(lines, "await pipe.ReadExactlyAsync");
        int yieldLine = FindSourceLine(lines,
            synchronousYield ? "yield return checked(value);" : "yield return CollectAndReturn");
        string interruptionSource = synchronousYield
            ? Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "DebuggerAsyncEnumerator.cs")
            : sourcePath;
        string[] interruptionLines = await File.ReadAllLinesAsync(interruptionSource, TestContext.CancellationToken)
            .ConfigureAwait(false);
        int interruptionLine = FindSourceLine(interruptionLines, synchronousYield ? "return pending;" : "GC.Collect(");
        string pipeName = $"cb-{Guid.NewGuid():N}";
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Task connection = pipe.WaitForConnectionAsync(TestContext.CancellationToken);
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        using DapTestCancellationCapture cancellationLog = CaptureProtocolOnCancellation(client);
        int initialThread = await LaunchToSourceBreakpointAsync(client, sourcePath, awaitLine,
            ["--debugger-async-iterator-step-fixture", pipeName], ResolveAsyncIteratorProgram(configuration),
            suppressJitOptimizations: true).ConfigureAwait(false);
        await connection.ConfigureAwait(false);
        Task<int> initialStep = StepAndReadStopAsync(client, "next", initialThread, TestContext.CancellationToken);
        byte[] suspended = new byte[1];
        await pipe.ReadExactlyAsync(suspended, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual((byte)1, suspended[0]);
        await pipe.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
        _ = await initialStep.ConfigureAwait(false);
        _ = await AssertSourcePolicyBreakpointAsync(client, sourcePath, yieldLine, verified: true).ConfigureAwait(false);
        int yieldThread = await ContinueEntryToUserBreakpointAsync(client).ConfigureAwait(false);
        await ClearSourceBreakpointsAsync(client, sourcePath).ConfigureAwait(false);
        _ = await AssertSourcePolicyBreakpointAsync(client, interruptionSource, interruptionLine, verified: true).ConfigureAwait(false);
        int interruptedThread = await StepAndReadStopAsync(client, "next", yieldThread,
            TestContext.CancellationToken, expectedReason: "breakpoint").ConfigureAwait(false);
        JsonElement frame = await ReadTopSourceFrameAsync(client, interruptedThread).ConfigureAwait(false);
        Assert.AreEqual(synchronousYield ? "Csls.TestProcessHost.DebuggerAsyncEnumerator.MoveNextAsync"
            : "Csls.TestProcessHost.DebuggerAsyncIteratorStepFixture.CollectAndReturn",
            frame.GetProperty("name").GetString());
        Assert.AreEqual(interruptionLine, frame.GetProperty("line").GetInt32());
        await ClearSourceBreakpointsAsync(client, interruptionSource).ConfigureAwait(false);
        int continueSequence = await client.SendRequestAsync("continue", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        await ReadSuccessfulTerminationAsync(client, continueSequence, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }
}
