namespace Csls.Debugger;

/// <summary>
/// Identifies an exact argument or local slot in a physical managed frame.
/// </summary>
/// <param name="ThreadId">The runtime thread owning the frame.</param>
/// <param name="StackStart">The physical frame stack-range start.</param>
/// <param name="StackEnd">The physical frame stack-range end.</param>
/// <param name="ModuleId">The exact loaded module identifier.</param>
/// <param name="MethodToken">The method-definition metadata token.</param>
/// <param name="ScopeKind">Whether the slot contains an argument or a local.</param>
/// <param name="Index">The physical zero-based slot index.</param>
internal sealed record ManagedFrameValueOrigin(
    int ThreadId,
    ulong StackStart,
    ulong StackEnd,
    int ModuleId,
    uint MethodToken,
    ManagedScopeKind ScopeKind,
    int Index) : ManagedValueOrigin;
