namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one managed IL instruction or an out-of-range placeholder.
/// </summary>
/// <param name="Address">The session-local virtual IL address.</param>
/// <param name="Bytes">The complete encoded instruction bytes.</param>
/// <param name="Instruction">The formatted ECMA-335 instruction.</param>
/// <param name="Symbol">The containing managed method name when requested.</param>
/// <param name="Source">The mapped source document when available.</param>
/// <param name="Line">The one-based source line, or zero.</param>
/// <param name="Column">The one-based source column, or zero.</param>
/// <param name="IsInvalid">Whether this is a required out-of-range placeholder.</param>
public sealed record DebugInstructionInfo(
    ulong Address,
    ReadOnlyMemory<byte> Bytes,
    string Instruction,
    string? Symbol,
    DebugSourceInfo? Source,
    int Line,
    int Column,
    bool IsInvalid);
