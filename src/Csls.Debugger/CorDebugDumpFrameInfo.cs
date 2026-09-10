using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Contains immediate frame values recovered from an immutable captured runtime.
/// </summary>
/// <param name="MethodToken">The runtime method definition token.</param>
/// <param name="StackPointer">The captured physical frame's stack pointer.</param>
/// <param name="IlOffset">The captured managed instruction offset.</param>
/// <param name="Values">The requested page of physical arguments or locals.</param>
public sealed record CorDebugDumpFrameInfo(uint MethodToken, ulong StackPointer, uint IlOffset,
    IReadOnlyList<DebugVariableInfo> Values);
