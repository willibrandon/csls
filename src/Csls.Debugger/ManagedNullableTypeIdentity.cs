using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Identifies intrinsic nullable storage through its runtime value kind, definition, and CoreLib base module.
/// </summary>
internal static class ManagedNullableTypeIdentity
{
    /// <summary>
    /// Verifies that a value is Nullable of one argument in the same loaded module as its intrinsic ValueType base.
    /// </summary>
    /// <param name="value">The original stopped value, without dereferencing or unboxing.</param>
    /// <param name="openModule">Opens metadata for the exact borrowed runtime module.</param>
    /// <returns>True only for the runtime's nullable value type.</returns>
    internal static unsafe bool IsNullable(nint value, Func<nint, PEReader> openModule)
    {
        ArgumentNullException.ThrowIfNull(openModule);
        if (ManagedRuntimeValueIdentity.GetElementType(value) != 0x11)
        {
            return false;
        }

        nint value2 = ComAbi.QueryInterface(value, ICorDebugValue2Abi.InterfaceId);
        nint exactType = 0;
        try
        {
            nint* typePointer = &exactType;
            int typeResult = new ICorDebugValue2Abi(value2).GetExactType((nint)typePointer);
            exactType = Volatile.Read(ref *typePointer);
            CorDebugHResult.ThrowIfFailed(typeResult, "ICorDebugValue2.GetExactType");
            RequirePointer(exactType, "ICorDebugValue2.GetExactType");
            return IsNullableType(exactType, openModule);
        }
        finally
        {
            Release(exactType);
            Release(value2);
        }
    }

    /// <summary>
    /// Verifies nullable type identity for formatting and inspection without relying on display names.
    /// </summary>
    /// <param name="exactType">The borrowed exact stopped runtime type.</param>
    /// <param name="openModule">Opens metadata for the exact borrowed runtime module.</param>
    /// <returns>True only for the runtime's nullable value type.</returns>
    internal static unsafe bool IsNullableType(nint exactType, Func<nint, PEReader> openModule)
    {
        ArgumentNullException.ThrowIfNull(openModule);
        uint elementType = 0;
        uint* elementTypePointer = &elementType;
        CorDebugHResult.ThrowIfFailed(new ICorDebugTypeAbi(exactType).GetType((nint)elementTypePointer),
            "ICorDebugType.GetType");
        if (Volatile.Read(ref *elementTypePointer) != 0x11)
        {
            return false;
        }

        nint runtimeClass = 0;
        nint module = 0;
        nint baseType = 0;
        nint baseClass = 0;
        nint baseModule = 0;
        try
        {
            nint* classPointer = &runtimeClass;
            int classResult = new ICorDebugTypeAbi(exactType).GetClass((nint)classPointer);
            runtimeClass = Volatile.Read(ref *classPointer);
            CorDebugHResult.ThrowIfFailed(classResult, "ICorDebugType.GetClass");
            RequirePointer(runtimeClass, "ICorDebugType.GetClass");

            nint* modulePointer = &module;
            int moduleResult = new ICorDebugClassAbi(runtimeClass).GetModule((nint)modulePointer);
            module = Volatile.Read(ref *modulePointer);
            CorDebugHResult.ThrowIfFailed(moduleResult, "ICorDebugClass.GetModule");
            RequirePointer(module, "ICorDebugClass.GetModule");

            using PEReader peReader = openModule(module);
            MetadataReader metadata = peReader.GetMetadataReader();
            TypeDefinition definition = ReadDefinition(runtimeClass, metadata);
            if (!IsSystemType(metadata, definition, "Nullable`1") || definition.GetGenericParameters().Count != 1)
            {
                return false;
            }

            nint* basePointer = &baseType;
            int baseResult = new ICorDebugTypeAbi(exactType).GetBase((nint)basePointer);
            baseType = Volatile.Read(ref *basePointer);
            CorDebugHResult.ThrowIfFailed(baseResult, "ICorDebugType.GetBase");
            if (baseType == 0)
            {
                return false;
            }

            nint* baseClassPointer = &baseClass;
            int baseClassResult = new ICorDebugTypeAbi(baseType).GetClass((nint)baseClassPointer);
            baseClass = Volatile.Read(ref *baseClassPointer);
            CorDebugHResult.ThrowIfFailed(baseClassResult, "ICorDebugType.GetClass");
            RequirePointer(baseClass, "ICorDebugType.GetClass");

            nint* baseModulePointer = &baseModule;
            int baseModuleResult = new ICorDebugClassAbi(baseClass).GetModule((nint)baseModulePointer);
            baseModule = Volatile.Read(ref *baseModulePointer);
            CorDebugHResult.ThrowIfFailed(baseModuleResult, "ICorDebugClass.GetModule");
            RequirePointer(baseModule, "ICorDebugClass.GetModule");

            return HaveSameModuleIdentity(module, baseModule) &&
                IsSystemType(metadata, ReadDefinition(baseClass, metadata), "ValueType");
        }
        finally
        {
            Release(baseModule);
            Release(baseClass);
            Release(baseType);
            Release(module);
            Release(runtimeClass);
        }
    }

    private static unsafe TypeDefinition ReadDefinition(nint runtimeClass, MetadataReader metadata)
    {
        uint token = 0;
        uint* tokenPointer = &token;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugClassAbi(runtimeClass).GetToken((nint)tokenPointer),
            "ICorDebugClass.GetToken");
        EntityHandle handle = MetadataTokens.EntityHandle(checked((int)Volatile.Read(ref *tokenPointer)));
        if (handle.Kind != HandleKind.TypeDefinition)
        {
            throw new BadImageFormatException("The runtime class has no metadata type definition.");
        }

        return metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
    }

    private static bool IsSystemType(MetadataReader metadata, TypeDefinition definition, string name) =>
        definition.GetDeclaringType().IsNil &&
        metadata.StringComparer.Equals(definition.Namespace, "System") &&
        metadata.StringComparer.Equals(definition.Name, name);

    private static bool HaveSameModuleIdentity(nint module, nint baseModule)
    {
        nint identity = ComAbi.GetIdentity(module);
        nint baseIdentity = 0;
        try
        {
            baseIdentity = ComAbi.GetIdentity(baseModule);
            return identity == baseIdentity;
        }
        finally
        {
            Release(baseIdentity);
            Release(identity);
        }
    }

    private static void RequirePointer(nint pointer, string operation)
    {
        if (pointer == 0)
        {
            throw new InvalidOperationException($"{operation} returned no runtime value.");
        }
    }

    private static void Release(nint pointer)
    {
        if (pointer != 0)
        {
            _ = ComAbi.Release(pointer);
        }
    }
}
