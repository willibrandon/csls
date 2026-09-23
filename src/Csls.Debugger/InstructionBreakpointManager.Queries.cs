using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Answers read-only managed-IL breakpoint queries.
/// </summary>
internal sealed partial class InstructionBreakpointManager
{
    /// <summary>
    /// Gets every logical instruction breakpoint ordered by session-local identifier.
    /// </summary>
    internal IReadOnlyList<DebugInstructionBreakpointInfo> GetBreakpoints()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _definitions
            .OrderBy(static definition => definition.Id)
            .Select(static definition => definition.ToInfo())
            .ToArray();
    }
}
