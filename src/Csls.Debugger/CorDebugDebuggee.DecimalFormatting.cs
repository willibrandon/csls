using Csls.Debugger.Interop;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Formats managed decimal values from their documented runtime field representation.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private string FormatDecimalValue(nint value, nint type)
    {
        nint instance = 0;
        nint runtimeClass = 0;
        nint module = 0;
        uint flags = 0;
        uint high = 0;
        uint middle = 0;
        uint low = 0;
        bool hasFlags = false;
        bool hasHigh = false;
        bool hasMiddle = false;
        bool hasLow = false;
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
                    ulong bits = ReadIntegralValueBits(fieldValue, out _);
                    switch (metadata.GetString(field.Name))
                    {
                        case "flags" or "_flags":
                            flags = checked((uint)bits);
                            hasFlags = true;
                            break;
                        case "hi" or "_hi32":
                            high = checked((uint)bits);
                            hasHigh = true;
                            break;
                        case "mid":
                            middle = checked((uint)bits);
                            hasMiddle = true;
                            break;
                        case "lo":
                            low = checked((uint)bits);
                            hasLow = true;
                            break;
                        case "_lo64":
                            low = unchecked((uint)bits);
                            middle = checked((uint)(bits >> 32));
                            hasLow = true;
                            hasMiddle = true;
                            break;
                    }
                }
                finally
                {
                    if (fieldValue != 0)
                    {
                        _ = ComAbi.Release(fieldValue);
                    }
                }
            }

            if (!hasFlags || !hasHigh || !hasMiddle || !hasLow)
            {
                throw new InvalidOperationException(
                    "System.Decimal does not expose its required runtime fields.");
            }

            const uint scaleMask = 0x00FF0000;
            const int scaleShift = 16;
            const uint signMask = 0x80000000;
            if ((flags & ~(scaleMask | signMask)) != 0 ||
                (flags & scaleMask) > 28U << scaleShift)
            {
                throw new InvalidOperationException(
                    "System.Decimal contains an invalid runtime representation.");
            }

            decimal formatted = new(
                unchecked((int)low),
                unchecked((int)middle),
                unchecked((int)high),
                (flags & signMask) != 0,
                checked((byte)((flags & scaleMask) >> scaleShift)));
            return formatted.ToString(CultureInfo.InvariantCulture);
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
}
