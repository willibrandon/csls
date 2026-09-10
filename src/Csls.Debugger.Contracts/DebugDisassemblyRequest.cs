namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects a bounded managed-IL instruction range from a stopped frame.
/// </summary>
/// <param name="InstructionReference">The opaque generation-bound IL location.</param>
/// <param name="ByteOffset">The signed byte offset applied before instruction selection.</param>
/// <param name="InstructionOffset">The signed instruction offset applied after the byte offset.</param>
/// <param name="InstructionCount">The exact number of instructions to return.</param>
/// <param name="ResolveSymbols">Whether metadata operands should include symbolic names.</param>
public sealed record DebugDisassemblyRequest(
    string InstructionReference,
    long ByteOffset,
    long InstructionOffset,
    int InstructionCount,
    bool ResolveSymbols);
