namespace Csls.Debugger;

/// <summary>
/// Identifies a physical managed activation independently of generation-owned COM wrappers.
/// </summary>
/// <param name="ThreadId">The runtime thread owning the activation.</param>
/// <param name="StackStart">The leafmost physical stack boundary.</param>
/// <param name="StackEnd">The rootmost physical stack boundary.</param>
/// <param name="ModuleId">The exact loaded module containing the method.</param>
/// <param name="MethodToken">The method-definition metadata token.</param>
internal sealed record ManagedFrameIdentity(
    int ThreadId,
    ulong StackStart,
    ulong StackEnd,
    int ModuleId,
    uint MethodToken);
