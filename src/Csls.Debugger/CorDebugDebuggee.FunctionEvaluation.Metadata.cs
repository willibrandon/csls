using Csls.Debugger.Contracts;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves managed function-evaluation targets from CLR metadata.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private static uint ResolveInstanceMethod(
        nint module,
        nint runtimeClass,
        string methodName,
        DebugExpressionLanguage language,
        ManagedExpressionValue[] arguments)
    {
        string modulePath = CorDebugModulePath.Get(module);
        using FileStream stream = File.OpenRead(modulePath);
        using var peReader = new PEReader(stream);
        MetadataReader metadata = peReader.GetMetadataReader();
        uint typeToken = GetClassToken(runtimeClass);
        StringComparison comparison = language == DebugExpressionLanguage.VisualBasic
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        while (typeToken != 0)
        {
            int row = checked((int)(typeToken & 0x00FFFFFF));
            if (row == 0 || row > metadata.TypeDefinitions.Count)
            {
                break;
            }

            TypeDefinition type = metadata.GetTypeDefinition(
                MetadataTokens.TypeDefinitionHandle(row));
            List<MethodDefinitionHandle> matches = [];
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
                (int parameterCount, bool isGeneric) = GetMethodSignatureInfo(
                    metadata,
                    method);
                if ((method.Attributes & (MethodAttributes.Static | MethodAttributes.Abstract)) != 0 ||
                    !string.Equals(metadata.GetString(method.Name), methodName, comparison) ||
                    isGeneric ||
                    parameterCount != arguments.Length)
                {
                    continue;
                }

                matches.Add(methodHandle);
            }

            if (matches.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Method call '{methodName}' with {arguments.Length} argument(s) is " +
                    "ambiguous on the runtime type.");
            }

            if (matches.Count == 1)
            {
                return checked((uint)MetadataTokens.GetToken(matches[0]));
            }

            EntityHandle baseType = type.BaseType;
            typeToken = baseType.Kind == HandleKind.TypeDefinition
                ? checked((uint)MetadataTokens.GetToken((TypeDefinitionHandle)baseType))
                : 0;
        }

        throw new InvalidOperationException(
            $"No instance method named '{methodName}' with {arguments.Length} argument(s) " +
            "is available on the runtime type in its defining module.");
    }

    private static (int ParameterCount, bool IsGeneric) GetMethodSignatureInfo(
        MetadataReader metadata,
        MethodDefinition method)
    {
        BlobReader signature = metadata.GetBlobReader(method.Signature);
        SignatureHeader header = signature.ReadSignatureHeader();
        if (header.IsGeneric)
        {
            _ = signature.ReadCompressedInteger();
        }

        return (signature.ReadCompressedInteger(), header.IsGeneric);
    }
}
