using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Identifies immutable captured storage independently of request-owned native type interfaces.
/// </summary>
/// <param name="TypeId">The opaque runtime identity originally read from the captured heap.</param>
/// <param name="Address">The captured value's storage address.</param>
/// <param name="Size">The remaining extent of its containing storage.</param>
/// <param name="HeapObject">Whether the address points to a managed heap object header.</param>
internal sealed record CorDebugDumpStorageLocation(CorDebugTypeId TypeId, ulong Address, ulong Size, bool HeapObject);
