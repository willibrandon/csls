using Csls.Debugger.Interop;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Formats common framework value types without executing code in the target process.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private const ulong DateTimeTicksMask = 0x3FFFFFFFFFFFFFFF;
    private const ulong DateTimeKindMask = 0xC000000000000000;
    private const ulong DateTimeUtcKind = 0x4000000000000000;
    private const ulong DateTimeLocalKind = 0x8000000000000000;
    private const long MaximumDateTimeTicks = 3155378975999999999;
    private const int MaximumDateTimeOffsetMinutes = 14 * 60;

    private bool TryFormatKnownFrameworkValue(
        nint value,
        nint type,
        string typeName,
        out string display)
    {
        display = typeName switch
        {
            "System.DateTime" => FormatDateTimeValue(value, type),
            "System.DateTimeOffset" => FormatDateTimeOffsetValue(value, type),
            "System.Guid" => FormatGuidValue(value, type),
            "System.TimeSpan" => FormatTimeSpanValue(value, type),
            _ => string.Empty
        };
        return display.Length > 0;
    }

    private string FormatDateTimeValue(nint value, nint type)
    {
        ulong data = ReadDateTimeData(value, type);
        long ticks = checked((long)(data & DateTimeTicksMask));
        ValidateDateTimeTicks(ticks);

        string timestamp = new DateTime(ticks, DateTimeKind.Unspecified).ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffff",
            CultureInfo.InvariantCulture);
        return (data & DateTimeKindMask) switch
        {
            DateTimeUtcKind => $"{timestamp}Z",
            DateTimeLocalKind or DateTimeKindMask => $"{timestamp} (Local)",
            _ => timestamp
        };
    }

    private string FormatTimeSpanValue(nint value, nint type)
    {
        bool found = false;
        long ticks = 0;
        VisitDeclaredRuntimeFields(value, type, (name, fieldValue) =>
        {
            if (name is "_ticks" or "ticks")
            {
                ticks = ReadSignedIntegralValue(fieldValue);
                found = true;
            }
        });
        if (!found)
        {
            throw new InvalidOperationException(
                "System.TimeSpan does not expose its required ticks field.");
        }

        return new TimeSpan(ticks).ToString("c", CultureInfo.InvariantCulture);
    }

    private string FormatGuidValue(nint value, nint type)
    {
        int first = 0;
        short second = 0;
        short third = 0;
        byte[] tail = new byte[8];
        ushort found = 0;
        VisitDeclaredRuntimeFields(value, type, (name, fieldValue) =>
        {
            ulong bits = ReadIntegralValueBits(fieldValue, out uint size);
            switch (name)
            {
                case "_a" when size == sizeof(int):
                    first = unchecked((int)(uint)bits);
                    found |= 1 << 0;
                    break;
                case "_b" when size == sizeof(short):
                    second = unchecked((short)(ushort)bits);
                    found |= 1 << 1;
                    break;
                case "_c" when size == sizeof(short):
                    third = unchecked((short)(ushort)bits);
                    found |= 1 << 2;
                    break;
                case "_d" when size == sizeof(byte):
                    tail[0] = checked((byte)bits);
                    found |= 1 << 3;
                    break;
                case "_e" when size == sizeof(byte):
                    tail[1] = checked((byte)bits);
                    found |= 1 << 4;
                    break;
                case "_f" when size == sizeof(byte):
                    tail[2] = checked((byte)bits);
                    found |= 1 << 5;
                    break;
                case "_g" when size == sizeof(byte):
                    tail[3] = checked((byte)bits);
                    found |= 1 << 6;
                    break;
                case "_h" when size == sizeof(byte):
                    tail[4] = checked((byte)bits);
                    found |= 1 << 7;
                    break;
                case "_i" when size == sizeof(byte):
                    tail[5] = checked((byte)bits);
                    found |= 1 << 8;
                    break;
                case "_j" when size == sizeof(byte):
                    tail[6] = checked((byte)bits);
                    found |= 1 << 9;
                    break;
                case "_k" when size == sizeof(byte):
                    tail[7] = checked((byte)bits);
                    found |= 1 << 10;
                    break;
            }
        });
        if (found != (1 << 11) - 1)
        {
            throw new InvalidOperationException(
                "System.Guid does not expose its required runtime fields.");
        }

        return new Guid(
            first,
            second,
            third,
            tail[0],
            tail[1],
            tail[2],
            tail[3],
            tail[4],
            tail[5],
            tail[6],
            tail[7]).ToString("D", CultureInfo.InvariantCulture);
    }

    private string FormatDateTimeOffsetValue(nint value, nint type)
    {
        bool foundDateTime = false;
        bool foundOffset = false;
        long utcTicks = 0;
        long offsetMinutes = 0;
        VisitDeclaredRuntimeFields(value, type, (name, fieldValue) =>
        {
            switch (name)
            {
                case "_dateTime" or "m_dateTime":
                    utcTicks = checked((long)(ReadDateTimeData(fieldValue) & DateTimeTicksMask));
                    foundDateTime = true;
                    break;
                case "_offsetMinutes" or "m_offsetMinutes":
                    offsetMinutes = ReadSignedIntegralValue(fieldValue);
                    foundOffset = true;
                    break;
            }
        });
        if (!foundDateTime || !foundOffset)
        {
            throw new InvalidOperationException(
                "System.DateTimeOffset does not expose its required runtime fields.");
        }

        ValidateDateTimeTicks(utcTicks);
        if (offsetMinutes is < -MaximumDateTimeOffsetMinutes or > MaximumDateTimeOffsetMinutes)
        {
            throw new InvalidOperationException(
                "System.DateTimeOffset contains an invalid UTC offset.");
        }

        var offset = TimeSpan.FromMinutes(offsetMinutes);
        var utc = new DateTimeOffset(utcTicks, TimeSpan.Zero);
        return utc.ToOffset(offset).ToString("O", CultureInfo.InvariantCulture);
    }

    private ulong ReadDateTimeData(nint value)
    {
        nint value2 = 0;
        nint type = 0;
        try
        {
            value2 = ComAbi.QueryInterface(value, ICorDebugValue2Abi.InterfaceId);
            unsafe
            {
                nint* typeAddress = &type;
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugValue2Abi(value2).GetExactType((nint)typeAddress),
                    "ICorDebugValue2.GetExactType");
                type = RequirePointer(
                    Volatile.Read(ref *typeAddress),
                    "ICorDebugValue2.GetExactType");
            }

            return ReadDateTimeData(value, type);
        }
        finally
        {
            if (type != 0)
            {
                _ = ComAbi.Release(type);
            }

            if (value2 != 0)
            {
                _ = ComAbi.Release(value2);
            }
        }
    }

    private ulong ReadDateTimeData(nint value, nint type)
    {
        bool found = false;
        ulong data = 0;
        VisitDeclaredRuntimeFields(value, type, (name, fieldValue) =>
        {
            if (name is "_dateData" or "dateData")
            {
                data = ReadIntegralValueBits(fieldValue, out uint size);
                if (size != sizeof(ulong))
                {
                    throw new InvalidOperationException(
                        "System.DateTime uses an unsupported date-data field width.");
                }

                found = true;
            }
        });
        if (!found)
        {
            throw new InvalidOperationException(
                "System.DateTime does not expose its required date-data field.");
        }

        return data;
    }

    private void VisitDeclaredRuntimeFields(
        nint value,
        nint type,
        Action<string, nint> visitor)
    {
        nint instance = 0;
        nint runtimeClass = 0;
        nint module = 0;
        try
        {
            instance = ComAbi.QueryInterface(value, ICorDebugObjectValueAbi.InterfaceId);
            runtimeClass = GetRuntimeTypeClass(type);
            module = GetClassModule(runtimeClass);
            using PEReader peReader = OpenRuntimeModule(module);
            MetadataReader metadata = peReader.GetMetadataReader();
            TypeDefinitionHandle handle = MetadataTokens.TypeDefinitionHandle(
                checked((int)(GetClassToken(runtimeClass) & 0x00FFFFFF)));
            foreach (FieldDefinitionHandle fieldHandle in metadata.GetTypeDefinition(handle).GetFields())
            {
                FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & FieldAttributes.Static) != 0)
                {
                    continue;
                }

                nint fieldValue = 0;
                try
                {
                    fieldValue = GetObjectFieldValue(
                        instance,
                        runtimeClass,
                        checked((uint)MetadataTokens.GetToken(fieldHandle)));
                    visitor(metadata.GetString(field.Name), fieldValue);
                }
                finally
                {
                    if (fieldValue != 0)
                    {
                        _ = ComAbi.Release(fieldValue);
                    }
                }
            }
        }
        finally
        {
            if (module != 0)
            {
                _ = ComAbi.Release(module);
            }

            if (runtimeClass != 0)
            {
                _ = ComAbi.Release(runtimeClass);
            }

            if (instance != 0)
            {
                _ = ComAbi.Release(instance);
            }
        }
    }

    private static long ReadSignedIntegralValue(nint value)
    {
        ulong bits = ReadIntegralValueBits(value, out uint size);
        return size switch
        {
            1 => unchecked((sbyte)(byte)bits),
            2 => unchecked((short)(ushort)bits),
            4 => unchecked((int)(uint)bits),
            8 => unchecked((long)bits),
            _ => throw new InvalidOperationException(
                $"The managed signed integral value has an unsupported size of {size} bytes.")
        };
    }

    private static void ValidateDateTimeTicks(long ticks)
    {
        if (ticks is < 0 or > MaximumDateTimeTicks)
        {
            throw new InvalidOperationException(
                "System.DateTime contains an invalid ticks value.");
        }
    }
}
