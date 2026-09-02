using Csls.Debugger.Interop;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Csls.Debugger;

/// <summary>
/// Formats immediate ICorDebug values without executing code in the target.
/// </summary>
internal static class CorDebugValueFormatter
{
    /// <summary>
    /// Formats one borrowed runtime value using language-neutral .NET literals.
    /// </summary>
    /// <param name="value">The borrowed ICorDebugValue pointer.</param>
    /// <returns>The immediate value and element-type display.</returns>
    internal static ManagedValueDisplay Format(nint value) => Format(value, depth: 0);

    private static unsafe ManagedValueDisplay Format(nint value, int depth)
    {
        const int maximumReferenceDepth = 8;
        if (depth >= maximumReferenceDepth)
        {
            return new ManagedValueDisplay("{...}", "object");
        }

        uint elementType = 0;
        uint* elementTypeAddress = &elementType;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugValueAbi(value).GetType((nint)elementTypeAddress),
            "ICorDebugValue.GetType");
        elementType = Volatile.Read(ref *elementTypeAddress);

        if (ComAbi.TryQueryInterface(value, ICorDebugReferenceValueAbi.InterfaceId, out nint reference))
        {
            try
            {
                int isNull = 0;
                int* isNullAddress = &isNull;
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugReferenceValueAbi(reference).IsNull((nint)isNullAddress),
                    "ICorDebugReferenceValue.IsNull");
                isNull = Volatile.Read(ref *isNullAddress);
                if (isNull != 0)
                {
                    return new ManagedValueDisplay("null", TypeName(elementType));
                }

                nint dereferenced = 0;
                nint* dereferencedAddress = &dereferenced;
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugReferenceValueAbi(reference).Dereference(
                        (nint)dereferencedAddress),
                    "ICorDebugReferenceValue.Dereference");
                dereferenced = Volatile.Read(ref *dereferencedAddress);
                if (dereferenced == 0)
                {
                    return new ManagedValueDisplay("null", TypeName(elementType));
                }

                try
                {
                    return Format(dereferenced, depth + 1);
                }
                finally
                {
                    _ = ComAbi.Release(dereferenced);
                }
            }
            finally
            {
                _ = ComAbi.Release(reference);
            }
        }

        if (ComAbi.TryQueryInterface(value, ICorDebugStringValueAbi.InterfaceId, out nint stringValue))
        {
            try
            {
                return new ManagedValueDisplay(ReadString(stringValue), "string");
            }
            finally
            {
                _ = ComAbi.Release(stringValue);
            }
        }

        if (!ComAbi.TryQueryInterface(value, ICorDebugGenericValueAbi.InterfaceId, out nint generic))
        {
            return new ManagedValueDisplay("{...}", TypeName(elementType));
        }

        try
        {
            uint size = 0;
            uint* sizeAddress = &size;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugValueAbi(value).GetSize((nint)sizeAddress),
                "ICorDebugValue.GetSize");
            size = Volatile.Read(ref *sizeAddress);
            if (size == 0 || size > 16)
            {
                return new ManagedValueDisplay("{...}", TypeName(elementType));
            }

            byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)size));
            fixed (byte* bytesAddress = bytes)
            {
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugGenericValueAbi(generic).GetValue((nint)bytesAddress),
                    "ICorDebugGenericValue.GetValue");
            }

            return new ManagedValueDisplay(FormatPrimitive(elementType, bytes), TypeName(elementType));
        }
        finally
        {
            _ = ComAbi.Release(generic);
        }
    }

    private static unsafe string ReadString(nint stringValue)
    {
        uint length = 0;
        uint* lengthAddress = &length;
        var api = new ICorDebugStringValueAbi(stringValue);
        CorDebugHResult.ThrowIfFailed(
            api.GetLength((nint)lengthAddress),
            "ICorDebugStringValue.GetLength");
        length = Volatile.Read(ref *lengthAddress);
        if (length > 1024 * 1024)
        {
            throw new InvalidOperationException(
                $"The target string exceeds the debugger limit of {1024 * 1024} characters.");
        }

        char[] characters = GC.AllocateUninitializedArray<char>(checked((int)length + 1));
        uint written = 0;
        uint* writtenAddress = &written;
        fixed (char* charactersAddress = characters)
        {
            CorDebugHResult.ThrowIfFailed(
                api.GetString(
                    checked(length + 1),
                    (nint)writtenAddress,
                    (nint)charactersAddress),
                "ICorDebugStringValue.GetString");
        }

        written = Volatile.Read(ref *writtenAddress);
        int characterCount = checked((int)Math.Min(length, written));
        return Quote(new string(characters, 0, characterCount));
    }

    private static string FormatPrimitive(uint elementType, ReadOnlySpan<byte> bytes) =>
        elementType switch
        {
            0x02 => bytes[0] == 0 ? "false" : "true",
            0x03 => $"'{(char)BinaryPrimitives.ReadUInt16LittleEndian(bytes)}'",
            0x04 => unchecked((sbyte)bytes[0]).ToString(CultureInfo.InvariantCulture),
            0x05 => bytes[0].ToString(CultureInfo.InvariantCulture),
            0x06 => BinaryPrimitives.ReadInt16LittleEndian(bytes).ToString(CultureInfo.InvariantCulture),
            0x07 => BinaryPrimitives.ReadUInt16LittleEndian(bytes).ToString(CultureInfo.InvariantCulture),
            0x08 => BinaryPrimitives.ReadInt32LittleEndian(bytes).ToString(CultureInfo.InvariantCulture),
            0x09 => BinaryPrimitives.ReadUInt32LittleEndian(bytes).ToString(CultureInfo.InvariantCulture),
            0x0a => BinaryPrimitives.ReadInt64LittleEndian(bytes).ToString(CultureInfo.InvariantCulture),
            0x0b => BinaryPrimitives.ReadUInt64LittleEndian(bytes).ToString(CultureInfo.InvariantCulture),
            0x0c => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes))
                .ToString("R", CultureInfo.InvariantCulture),
            0x0d => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes))
                .ToString("R", CultureInfo.InvariantCulture),
            0x18 => IntPtr.Size == 8
                ? BinaryPrimitives.ReadInt64LittleEndian(bytes).ToString(CultureInfo.InvariantCulture)
                : BinaryPrimitives.ReadInt32LittleEndian(bytes).ToString(CultureInfo.InvariantCulture),
            0x19 => IntPtr.Size == 8
                ? BinaryPrimitives.ReadUInt64LittleEndian(bytes).ToString(CultureInfo.InvariantCulture)
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes).ToString(CultureInfo.InvariantCulture),
            _ => $"{{{Convert.ToHexString(bytes)}}}"
        };

    private static string TypeName(uint elementType) =>
        elementType switch
        {
            0x02 => "bool",
            0x03 => "char",
            0x04 => "sbyte",
            0x05 => "byte",
            0x06 => "short",
            0x07 => "ushort",
            0x08 => "int",
            0x09 => "uint",
            0x0a => "long",
            0x0b => "ulong",
            0x0c => "float",
            0x0d => "double",
            0x0e => "string",
            0x18 => "nint",
            0x19 => "nuint",
            0x1c => "object",
            _ => $"element-type 0x{elementType:X2}"
        };

    private static string Quote(string value)
    {
        var result = new StringBuilder(value.Length + 2);
        result.Append('"');
        foreach (char character in value)
        {
            result.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ => character.ToString()
            });
        }

        return result.Append('"').ToString();
    }
}
