namespace Csls.Debugger;

/// <summary>
/// Handles managed breakpoint callbacks and logical stop predicates.
/// </summary>
internal sealed partial class CorDebugManagedCallback
{
    private async ValueTask<bool> HandleBreakpointAsync(
        nint thread,
        nint breakpoint,
        CancellationToken cancellationToken)
    {
        if (thread == 0 || breakpoint == 0)
        {
            return true;
        }

        int managedThreadId = checked((int)GetThreadId(thread));
        ManagedTargetBreakpointDecision targetDecision = await _targetBreakpointReached(
            managedThreadId,
            breakpoint,
            cancellationToken).ConfigureAwait(false);
        if (targetDecision != ManagedTargetBreakpointDecision.Unrecognized)
        {
            return targetDecision == ManagedTargetBreakpointDecision.Continue;
        }

        DebugBreakpointKind kind;
        bool shouldBreak;
        if (_sourceBreakpoints.GetBreakDecision(breakpoint) is bool sourceDecision)
        {
            kind = DebugBreakpointKind.Source;
            shouldBreak = sourceDecision;
        }
        else if (_functionBreakpoints.GetBreakDecision(breakpoint) is bool functionDecision)
        {
            kind = DebugBreakpointKind.Function;
            shouldBreak = functionDecision;
        }
        else
        {
            return true;
        }

        if (!shouldBreak)
        {
            return true;
        }

        await _breakpointStopped(managedThreadId, kind, cancellationToken)
            .ConfigureAwait(false);
        return false;
    }
}
