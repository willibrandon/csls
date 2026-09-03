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

        uint threadId = GetThreadId(thread);
        await _breakpointStopped(checked((int)threadId), kind, cancellationToken)
            .ConfigureAwait(false);
        return false;
    }
}
