using System.Reflection.Emit;

namespace Csls.Debugger;

/// <summary>
/// Decodes complete ECMA-335 method bodies without loading target assemblies.
/// </summary>
internal static class ManagedIlDecoder
{
    /// <summary>
    /// Decodes every instruction from an immutable method-body byte sequence.
    /// </summary>
    /// <param name="bytes">The method-body IL bytes.</param>
    /// <returns>The ordered decoded instructions.</returns>
    internal static IReadOnlyList<ManagedIlInstruction> Decode(ReadOnlySpan<byte> bytes)
    {
        var result = new List<ManagedIlInstruction>();
        int position = 0;
        while (position < bytes.Length)
        {
            int start = position;
            ushort encoded = ReadOpCode(bytes, ref position);
            if (!ManagedIlOpCodeCatalog.TryGet(encoded, out OpCode opCode))
            {
                result.Add(new ManagedIlInstruction(
                    start,
                    bytes[start..position].ToArray(),
                    $".byte 0x{bytes[start]:X2}",
                    string.Empty,
                    MetadataToken: null));
                continue;
            }

            try
            {
                string operand = ManagedIlOperandReader.Read(
                    opCode.OperandType,
                    bytes,
                    ref position,
                    out int? metadataToken);
                result.Add(new ManagedIlInstruction(
                    start,
                    bytes[start..position].ToArray(),
                    opCode.Name ?? $"opcode 0x{encoded:X4}",
                    operand,
                    metadataToken));
            }
            catch (BadImageFormatException)
            {
                result.Add(new ManagedIlInstruction(
                    start,
                    bytes[start..].ToArray(),
                    "<invalid IL>",
                    string.Empty,
                    MetadataToken: null));
                break;
            }
        }

        return result;
    }

    private static ushort ReadOpCode(ReadOnlySpan<byte> bytes, ref int position)
    {
        byte first = bytes[position++];
        if (first != 0xFE)
        {
            return first;
        }

        if (position == bytes.Length)
        {
            return first;
        }

        return checked((ushort)((first << 8) | bytes[position++]));
    }
}
