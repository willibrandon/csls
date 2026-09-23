namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects a bounded memory range relative to an opaque stopped-state handle.
/// </summary>
/// <param name="MemoryReference">The opaque generation-bound memory handle.</param>
/// <param name="Offset">The signed byte offset from the handle's base address.</param>
/// <param name="Count">The requested byte count.</param>
public sealed record DebugMemoryReadRequest(string MemoryReference, long Offset, int Count);
