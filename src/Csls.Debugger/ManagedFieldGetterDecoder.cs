using System.Buffers.Binary;

namespace Csls.Debugger;

/// <summary>
/// Recognizes complete field-return IL bodies with bounded compiler-generated return-local control flow.
/// </summary>
internal static class ManagedFieldGetterDecoder
{
    /// <summary>
    /// Bounds the current IL copied and inspected for one fast getter candidate.
    /// </summary>
    internal const int MaximumMethodBodyBytes = 256;

    /// <summary>
    /// Recognizes a complete field-return body and identifies the storage signatures its caller must validate.
    /// </summary>
    /// <param name="body">The complete IL bytes obtained from a real method body.</param>
    /// <param name="fieldToken">Receives the exact field operand on successful recognition.</param>
    /// <param name="returnLocal">Receives the return-local index, or minus one for a direct return.</param>
    /// <returns>True when every instruction belongs to the accepted field-return pattern.</returns>
    internal static bool TryDecode(ReadOnlySpan<byte> body, out int fieldToken, out int returnLocal)
    {
        fieldToken = 0;
        returnLocal = -1;
        if (body.Length > MaximumMethodBodyBytes)
        {
            return false;
        }
        int offset = 0;
        if (!Read(body, ref offset, 0x02) || !Read(body, ref offset, 0x7b) || body.Length - offset < sizeof(int))
        {
            return false;
        }
        int token = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
        if ((token & 0x00ffffff) == 0 || (token & unchecked((int)0xff000000)) is not (0x04000000 or 0x0a000000))
        {
            return false;
        }
        offset += sizeof(int);
        SkipNops(body, ref offset);
        int selectedLocal = -1;
        if (offset < body.Length && body[offset] is >= 0x0a and <= 0x0d)
        {
            byte local = checked((byte)(body[offset++] - 0x0a));
            selectedLocal = local;
            SkipNops(body, ref offset);
            if (offset < body.Length && body[offset] is 0x2b or 0x38)
            {
                bool shortBranch = body[offset++] == 0x2b;
                int size = shortBranch ? 1 : sizeof(int);
                if (body.Length - offset < size)
                {
                    return false;
                }
                int distance = shortBranch ? unchecked((sbyte)body[offset]) : BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
                offset += size;
                if (distance < 0 || distance > body.Length - offset ||
                    body.Slice(offset, distance).ContainsAnyExcept((byte)0))
                {
                    return false;
                }
                offset += distance;
            }
            if (!Read(body, ref offset, checked((byte)(0x06 + local))))
            {
                return false;
            }
        }
        if (!Read(body, ref offset, 0x2a))
        {
            return false;
        }
        SkipNops(body, ref offset);
        if (offset != body.Length)
        {
            return false;
        }
        fieldToken = token;
        returnLocal = selectedLocal;
        return true;
    }

    private static bool Read(ReadOnlySpan<byte> body, ref int offset, byte instruction)
    {
        SkipNops(body, ref offset);
        return offset < body.Length && body[offset++] == instruction;
    }

    private static void SkipNops(ReadOnlySpan<byte> body, ref int offset)
    {
        while (offset < body.Length && body[offset] == 0)
        {
            offset++;
        }
    }
}
