using System.Buffers.Binary;
using System.Globalization;
using System.Reflection.Emit;

namespace Csls.Debugger;

/// <summary>
/// Reads and formats every ECMA-335 operand encoding with strict bounds checks.
/// </summary>
internal static class ManagedIlOperandReader
{
    /// <summary>
    /// Reads one operand and advances the method-body position.
    /// </summary>
    /// <param name="operandType">The runtime operand encoding.</param>
    /// <param name="bytes">The complete method-body IL bytes.</param>
    /// <param name="position">The first operand byte and resulting next instruction.</param>
    /// <param name="metadataToken">Receives a metadata token when encoded.</param>
    /// <returns>The invariant formatted operand.</returns>
    internal static string Read(
        OperandType operandType,
        ReadOnlySpan<byte> bytes,
        ref int position,
        out int? metadataToken)
    {
        metadataToken = null;
        return operandType switch
        {
            OperandType.InlineNone => string.Empty,
            OperandType.ShortInlineI => ReadSByte(bytes, ref position)
                .ToString(CultureInfo.InvariantCulture),
            OperandType.InlineI => ReadInt32(bytes, ref position)
                .ToString(CultureInfo.InvariantCulture),
            OperandType.InlineI8 => ReadInt64(bytes, ref position)
                .ToString(CultureInfo.InvariantCulture),
            OperandType.ShortInlineR => ReadSingle(bytes, ref position)
                .ToString("R", CultureInfo.InvariantCulture),
            OperandType.InlineR => ReadDouble(bytes, ref position)
                .ToString("R", CultureInfo.InvariantCulture),
            OperandType.ShortInlineVar => ReadByte(bytes, ref position)
                .ToString(CultureInfo.InvariantCulture),
            OperandType.InlineVar => ReadUInt16(bytes, ref position)
                .ToString(CultureInfo.InvariantCulture),
            OperandType.ShortInlineBrTarget => FormatShortBranch(bytes, ref position),
            OperandType.InlineBrTarget => FormatBranch(bytes, ref position),
            OperandType.InlineSwitch => FormatSwitch(bytes, ref position),
            OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineSig or
            OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType =>
                FormatToken(bytes, ref position, out metadataToken),
            _ => throw new BadImageFormatException(
                $"Unsupported IL operand encoding {operandType}.")
        };
    }

    private static string FormatShortBranch(ReadOnlySpan<byte> bytes, ref int position)
    {
        int delta = ReadSByte(bytes, ref position);
        return FormatLabel(checked(position + delta));
    }

    private static string FormatBranch(ReadOnlySpan<byte> bytes, ref int position)
    {
        int delta = ReadInt32(bytes, ref position);
        return FormatLabel(checked(position + delta));
    }

    private static string FormatSwitch(ReadOnlySpan<byte> bytes, ref int position)
    {
        int count = ReadInt32(bytes, ref position);
        if (count < 0 || count > (bytes.Length - position) / sizeof(int))
        {
            throw new BadImageFormatException("The IL switch table exceeds the method body.");
        }

        int[] deltas = new int[count];
        for (int index = 0; index < count; index++)
        {
            deltas[index] = ReadInt32(bytes, ref position);
        }

        int nextInstruction = position;
        return $"({string.Join(", ", deltas.Select(delta =>
            FormatLabel(checked(nextInstruction + delta))))})";
    }

    private static string FormatToken(
        ReadOnlySpan<byte> bytes,
        ref int position,
        out int? metadataToken)
    {
        int token = ReadInt32(bytes, ref position);
        metadataToken = token;
        return $"0x{unchecked((uint)token):X8}";
    }

    private static string FormatLabel(int offset) => $"IL_{unchecked((uint)offset):X4}";

    private static byte ReadByte(ReadOnlySpan<byte> bytes, ref int position)
    {
        Ensure(bytes, position, sizeof(byte));
        return bytes[position++];
    }

    private static sbyte ReadSByte(ReadOnlySpan<byte> bytes, ref int position) =>
        unchecked((sbyte)ReadByte(bytes, ref position));

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, ref int position)
    {
        Ensure(bytes, position, sizeof(ushort));
        ushort result = BinaryPrimitives.ReadUInt16LittleEndian(bytes[position..]);
        position += sizeof(ushort);
        return result;
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, ref int position)
    {
        Ensure(bytes, position, sizeof(int));
        int result = BinaryPrimitives.ReadInt32LittleEndian(bytes[position..]);
        position += sizeof(int);
        return result;
    }

    private static long ReadInt64(ReadOnlySpan<byte> bytes, ref int position)
    {
        Ensure(bytes, position, sizeof(long));
        long result = BinaryPrimitives.ReadInt64LittleEndian(bytes[position..]);
        position += sizeof(long);
        return result;
    }

    private static float ReadSingle(ReadOnlySpan<byte> bytes, ref int position) =>
        BitConverter.Int32BitsToSingle(ReadInt32(bytes, ref position));

    private static double ReadDouble(ReadOnlySpan<byte> bytes, ref int position) =>
        BitConverter.Int64BitsToDouble(ReadInt64(bytes, ref position));

    private static void Ensure(ReadOnlySpan<byte> bytes, int position, int count)
    {
        if (position < 0 || count < 0 || position > bytes.Length - count)
        {
            throw new BadImageFormatException("The IL operand exceeds the method body.");
        }
    }
}
