using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves compiler-lowered managed type names to exact CoreCLR runtime types.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private const uint ArrayElementType = 0x14;
    private const uint ClassElementType = 0x12;
    private const uint SingleDimensionArrayElementType = 0x1D;
    private const uint ValueTypeElementType = 0x11;

    private unsafe nint ResolveRuntimeType(
        ManagedRuntimeTypeReference reference,
        DebugExpressionLanguage language,
        nint thread,
        string operation)
    {
        (CorDebugLoadedModule module, uint typeToken) = ResolveLoadedRuntimeType(
            reference.MetadataName,
            language,
            operation);
        using PEReader? peReader = module.OpenPeReader();
        if (peReader is null)
        {
            throw new InvalidOperationException(
                $"Loaded module '{module.Name ?? "unnamed module"}' no longer has a " +
                "readable PE image.");
        }

        MetadataReader metadata = peReader.GetMetadataReader();
        TypeDefinitionHandle typeHandle = MetadataTokens.TypeDefinitionHandle(
            checked((int)(typeToken & 0x00FFFFFF)));
        TypeDefinition definition = metadata.GetTypeDefinition(typeHandle);
        int expectedArgumentCount = definition.GetGenericParameters().Count;
        if (expectedArgumentCount != reference.TypeArguments.Count)
        {
            throw new InvalidOperationException(
                $"Runtime type '{reference.DebuggerTypeName}' requires " +
                $"{expectedArgumentCount} generic argument(s), but " +
                $"{reference.TypeArguments.Count} were supplied.");
        }

        nint runtimeClass = 0;
        nint runtimeClass2 = 0;
        nint result = 0;
        nint[] typeArguments = new nint[reference.TypeArguments.Count];
        try
        {
            for (int index = 0; index < typeArguments.Length; index++)
            {
                typeArguments[index] = ResolveRuntimeType(
                    reference.TypeArguments[index],
                    language,
                    thread,
                    operation);
            }

            runtimeClass = GetModuleClass(module.Pointer, typeToken);
            runtimeClass2 = ComAbi.QueryInterface(
                runtimeClass,
                ICorDebugClass2Abi.InterfaceId);
            fixed (nint* typeArgumentsAddress = typeArguments)
            {
                nint* resultAddress = &result;
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugClass2Abi(runtimeClass2).GetParameterizedType(
                        IsValueTypeDefinition(metadata, definition)
                            ? ValueTypeElementType
                            : ClassElementType,
                        checked((uint)typeArguments.Length),
                        typeArguments.Length == 0 ? 0 : (nint)typeArgumentsAddress,
                        (nint)resultAddress),
                    "ICorDebugClass2.GetParameterizedType");
                result = RequirePointer(
                    Volatile.Read(ref *resultAddress),
                    "ICorDebugClass2.GetParameterizedType");
            }

            if (reference.ArrayRanks.Count != 0)
            {
                nint elementType = result;
                result = 0;
                result = ApplyRuntimeArrayRanks(elementType, reference.ArrayRanks, thread);
            }

            return result;
        }
        catch
        {
            if (result != 0)
            {
                _ = ComAbi.Release(result);
            }

            throw;
        }
        finally
        {
            foreach (nint typeArgument in typeArguments.Where(
                static typeArgument => typeArgument != 0))
            {
                _ = ComAbi.Release(typeArgument);
            }

            if (runtimeClass2 != 0)
            {
                _ = ComAbi.Release(runtimeClass2);
            }

            if (runtimeClass != 0)
            {
                _ = ComAbi.Release(runtimeClass);
            }
        }
    }

    private static unsafe nint ApplyRuntimeArrayRanks(
        nint elementType,
        IReadOnlyList<int> ranks,
        nint thread)
    {
        nint appDomain = 0;
        nint appDomain2 = 0;
        nint currentType = elementType;
        try
        {
            nint* appDomainAddress = &appDomain;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugThreadAbi(thread).GetAppDomain((nint)appDomainAddress),
                "ICorDebugThread.GetAppDomain");
            appDomain = RequirePointer(
                Volatile.Read(ref *appDomainAddress),
                "ICorDebugThread.GetAppDomain");
            appDomain2 = ComAbi.QueryInterface(
                appDomain,
                ICorDebugAppDomain2Abi.InterfaceId);
            foreach (int rank in ranks)
            {
                nint arrayType = 0;
                nint* arrayTypeAddress = &arrayType;
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugAppDomain2Abi(appDomain2).GetArrayOrPointerType(
                        rank == 1
                            ? SingleDimensionArrayElementType
                            : ArrayElementType,
                        checked((uint)rank),
                        currentType,
                        (nint)arrayTypeAddress),
                    "ICorDebugAppDomain2.GetArrayOrPointerType");
                arrayType = RequirePointer(
                    Volatile.Read(ref *arrayTypeAddress),
                    "ICorDebugAppDomain2.GetArrayOrPointerType");
                _ = ComAbi.Release(currentType);
                currentType = arrayType;
            }

            return currentType;
        }
        catch
        {
            if (currentType != 0)
            {
                _ = ComAbi.Release(currentType);
            }

            throw;
        }
        finally
        {
            if (appDomain2 != 0)
            {
                _ = ComAbi.Release(appDomain2);
            }

            if (appDomain != 0)
            {
                _ = ComAbi.Release(appDomain);
            }
        }
    }

    private static bool IsValueTypeDefinition(
        MetadataReader metadata,
        TypeDefinition definition) => definition.BaseType.Kind switch
        {
            HandleKind.TypeReference => IsSystemValueTypeBase(
                metadata,
                metadata.GetTypeReference((TypeReferenceHandle)definition.BaseType)),
            HandleKind.TypeDefinition => IsSystemValueTypeBase(
                metadata,
                metadata.GetTypeDefinition((TypeDefinitionHandle)definition.BaseType)),
            _ => false
        };

    private static bool IsSystemValueTypeBase(
        MetadataReader metadata,
        TypeReference type) =>
        IsNamedType(metadata, type, "System", "ValueType") ||
        IsNamedType(metadata, type, "System", "Enum");

    private static bool IsSystemValueTypeBase(
        MetadataReader metadata,
        TypeDefinition type) =>
        IsNamedType(metadata, type, "System", "ValueType") ||
        IsNamedType(metadata, type, "System", "Enum");
}
