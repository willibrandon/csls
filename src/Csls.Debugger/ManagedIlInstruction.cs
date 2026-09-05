namespace Csls.Debugger;

/// <summary>
/// Represents one decoded ECMA-335 instruction and its metadata operand token.
/// </summary>
/// <param name="Offset">The zero-based method-body IL offset.</param>
/// <param name="Bytes">The complete encoded instruction bytes.</param>
/// <param name="Name">The canonical lowercase IL operation name.</param>
/// <param name="Operand">The formatted operand without a leading separator.</param>
/// <param name="MetadataToken">The metadata operand token when present.</param>
internal sealed record ManagedIlInstruction(
    int Offset,
    ReadOnlyMemory<byte> Bytes,
    string Name,
    string Operand,
    int? MetadataToken);
