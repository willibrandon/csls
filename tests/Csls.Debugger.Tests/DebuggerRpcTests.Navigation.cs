using Csls.Debugger.Contracts;
using Csls.Debugger.Control;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies private-RPC source-aware execution navigation.
/// </summary>
public sealed partial class DebuggerRpcTests
{
    private static async Task AssertRpcNavigationAsync(
        DebuggerRpcClient client,
        DebugStackFrameInfo frame,
        DebugSessionSnapshot stopped,
        string sourcePath,
        int localLine,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DebugStepTargetInfo> stepTargets = await client.GetStepTargetsAsync(
            new DebugStepTargetsRequest(frame.Id),
            cancellationToken).ConfigureAwait(false);
        Assert.HasCount(0, stepTargets);

        IReadOnlyList<DebugGotoTargetInfo> gotoTargets = await client.GetGotoTargetsAsync(
            new DebugGotoTargetsRequest(frame.Id, sourcePath, localLine, Column: null),
            cancellationToken).ConfigureAwait(false);
        Assert.HasCount(1, gotoTargets);
        DebugGotoTargetInfo target = gotoTargets[0];
        Assert.AreEqual(localLine, target.Line);
        Assert.IsFalse(string.IsNullOrWhiteSpace(target.InstructionReference));
        int threadId = stopped.StoppedThreadId ?? throw new AssertFailedException("The goto stop has no thread identifier.");
        DebugSessionSnapshot moved = await client.GotoAsync(
            new DebugGotoRequest(threadId, target.Id),
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Stopped, moved.State);
        Assert.AreEqual("goto", moved.StopReason);
        Assert.IsGreaterThan(stopped.StopGeneration, moved.StopGeneration);
    }
}
