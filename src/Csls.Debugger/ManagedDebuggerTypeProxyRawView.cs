namespace Csls.Debugger;

/// <summary>
/// Describes the retained original object exposed after successful proxy expansion.
/// </summary>
/// <param name="Pointer">The borrowed retained runtime-value pointer.</param>
/// <param name="VariablesReference">The generation-owned raw expansion reference.</param>
/// <param name="MemoryReference">The optional stopped-state memory reference.</param>
internal sealed record ManagedDebuggerTypeProxyRawView(
    nint Pointer,
    int VariablesReference,
    string? MemoryReference);
