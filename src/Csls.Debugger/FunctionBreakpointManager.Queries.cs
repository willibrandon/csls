using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Answers read-only managed function-breakpoint queries.
/// </summary>
internal sealed partial class FunctionBreakpointManager
{
    /// <summary>
    /// Gets every logical function breakpoint ordered by session-local identifier.
    /// </summary>
    internal IReadOnlyList<DebugFunctionBreakpointInfo> GetBreakpoints()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _definitions
            .OrderBy(static definition => definition.Id)
            .Select(static definition => definition.ToInfo())
            .ToArray();
    }
}
