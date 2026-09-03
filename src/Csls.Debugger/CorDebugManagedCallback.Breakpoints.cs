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

        ManagedBreakpointHit? hit = null;
        if (_sourceBreakpoints.FindDefinition(breakpoint) is
            IManagedBreakpointDefinition sourceDefinition)
        {
            hit = new ManagedBreakpointHit(DebugBreakpointKind.Source, sourceDefinition);
        }
        else if (_functionBreakpoints.FindDefinition(breakpoint) is
            IManagedBreakpointDefinition functionDefinition)
        {
            hit = new ManagedBreakpointHit(DebugBreakpointKind.Function, functionDefinition);
        }
        else if (_instructionBreakpoints.FindDefinition(breakpoint) is
            IManagedBreakpointDefinition instructionDefinition)
        {
            hit = new ManagedBreakpointHit(
                DebugBreakpointKind.Instruction,
                instructionDefinition);
        }

        if (hit is null)
        {
            return true;
        }

        return await _breakpointReached(managedThreadId, hit, cancellationToken)
            .ConfigureAwait(false);
    }
}
