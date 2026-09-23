using Csls.Debugger.Interop;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Reads compiler and runtime continuation fields without executing target code.
/// </summary>
internal sealed class ManagedContinuationObjectReader
{
    private readonly Func<nint, CorDebugLoadedModule?> _moduleResolver;
    private readonly Func<nint, nint> _dereference;

    /// <summary>
    /// Creates a reader over the stopped process's retained modules and value interfaces.
    /// </summary>
    internal ManagedContinuationObjectReader(
        Func<nint, CorDebugLoadedModule?> moduleResolver, Func<nint, nint> dereference)
    {
        _moduleResolver = moduleResolver;
        _dereference = dereference;
    }

    /// <summary>
    /// Reads an owned instance field, optionally requiring its declaring CoreLib type.
    /// </summary>
    internal unsafe nint ReadField(nint value, string name, string? coreType = null)
    {
        nint dereferenced = 0;
        nint instance = 0;
        nint value2 = 0;
        nint type = 0;
        try
        {
            dereferenced = _dereference(value);
            instance = ComAbi.QueryInterface(dereferenced, ICorDebugObjectValueAbi.InterfaceId);
            value2 = ComAbi.QueryInterface(dereferenced, ICorDebugValue2Abi.InterfaceId);
            nint* typeAddress = &type;
            int result = new ICorDebugValue2Abi(value2).GetExactType((nint)typeAddress);
            type = Volatile.Read(ref *typeAddress);
            CorDebugHResult.ThrowIfFailed(result, "ICorDebugValue2.GetExactType");
            for (int depth = 0; type != 0 && depth < 64; depth++)
            {
                nint runtimeClass = 0;
                nint nextType = 0;
                try
                {
                    nint* classAddress = &runtimeClass;
                    result = new ICorDebugTypeAbi(type).GetClass((nint)classAddress);
                    runtimeClass = Volatile.Read(ref *classAddress);
                    CorDebugHResult.ThrowIfFailed(result, "ICorDebugType.GetClass");
                    (CorDebugLoadedModule module, uint token) = ResolveClass(runtimeClass);
                    using PEReader? image = module.OpenPeReader();
                    if (image is null)
                    {
                        return 0;
                    }

                    MetadataReader metadata = image.GetMetadataReader();
                    TypeDefinition definition = metadata.GetTypeDefinition(
                        MetadataTokens.TypeDefinitionHandle(checked((int)(token & 0x00ffffff))));
                    bool matches = coreType is null ||
                        (metadata.IsAssembly && metadata.StringComparer.Equals(
                            metadata.GetAssemblyDefinition().Name, "System.Private.CoreLib") &&
                        metadata.StringComparer.Equals(definition.Name, coreType));
                    if (matches)
                    {
                        foreach (FieldDefinitionHandle handle in definition.GetFields())
                        {
                            FieldDefinition field = metadata.GetFieldDefinition(handle);
                            if ((field.Attributes & FieldAttributes.Static) != 0 ||
                                !metadata.StringComparer.Equals(field.Name, name))
                            {
                                continue;
                            }

                            nint fieldValue = 0;
                            nint* fieldAddress = &fieldValue;
                            result = new ICorDebugObjectValueAbi(instance).GetFieldValue(runtimeClass,
                                checked((uint)MetadataTokens.GetToken(handle)), (nint)fieldAddress);
                            fieldValue = Volatile.Read(ref *fieldAddress);
                            if (result < 0)
                            {
                                Release(fieldValue);
                                CorDebugHResult.ThrowIfFailed(result, "ICorDebugObjectValue.GetFieldValue");
                            }

                            return fieldValue;
                        }
                    }

                    nint* nextAddress = &nextType;
                    result = new ICorDebugTypeAbi(type).GetBase((nint)nextAddress);
                    nextType = Volatile.Read(ref *nextAddress);
                    CorDebugHResult.ThrowIfFailed(result, "ICorDebugType.GetBase");
                }
                finally
                {
                    Release(runtimeClass);
                    Release(type);
                    type = nextType;
                }
            }

            return 0;
        }
        finally
        {
            Release(type);
            Release(value2);
            Release(instance);
            Release(dereferenced);
        }
    }

    /// <summary>
    /// Resolves the loaded module and exact declaring type for an object or struct value.
    /// </summary>
    internal unsafe (CorDebugLoadedModule Module, uint TypeToken) ResolveValue(nint value)
    {
        nint dereferenced = 0;
        nint instance = 0;
        nint runtimeClass = 0;
        try
        {
            dereferenced = _dereference(value);
            instance = ComAbi.QueryInterface(dereferenced, ICorDebugObjectValueAbi.InterfaceId);
            nint* classAddress = &runtimeClass;
            int result = new ICorDebugObjectValueAbi(instance).GetClass((nint)classAddress);
            runtimeClass = Volatile.Read(ref *classAddress);
            CorDebugHResult.ThrowIfFailed(result, "ICorDebugObjectValue.GetClass");
            return ResolveClass(runtimeClass);
        }
        finally
        {
            Release(runtimeClass);
            Release(instance);
            Release(dereferenced);
        }
    }

    /// <summary>
    /// Creates an owned strong runtime handle that follows a continuation box through collection.
    /// </summary>
    internal unsafe nint CreateHandle(nint value)
    {
        nint dereferenced = 0;
        nint heap = 0;
        nint handle = 0;
        try
        {
            dereferenced = _dereference(value);
            heap = ComAbi.QueryInterface(dereferenced, ICorDebugHeapValue2Abi.InterfaceId);
            nint* handleAddress = &handle;
            int result = new ICorDebugHeapValue2Abi(heap).CreateHandle(1, (nint)handleAddress);
            handle = Volatile.Read(ref *handleAddress);
            CorDebugHResult.ThrowIfFailed(result, "ICorDebugHeapValue2.CreateHandle");
            nint owned = handle;
            handle = 0;
            return owned;
        }
        finally
        {
            if (handle != 0)
            {
                _ = new ICorDebugHandleValueAbi(handle).Dispose();
                Release(handle);
            }

            Release(heap);
            Release(dereferenced);
        }
    }

    /// <summary>
    /// Reads the current address of a reference target or by-reference struct's storage.
    /// </summary>
    internal unsafe ulong GetAddress(nint value)
    {
        nint dereferenced = _dereference(value);
        try
        {
            ulong address = 0;
            ulong* addressPointer = &address;
            CorDebugHResult.ThrowIfFailed(new ICorDebugValueAbi(dereferenced).GetAddress((nint)addressPointer),
                "ICorDebugValue.GetAddress");
            return Volatile.Read(ref *addressPointer);
        }
        finally
        {
            Release(dereferenced);
        }
    }

    private unsafe (CorDebugLoadedModule Module, uint TypeToken) ResolveClass(nint runtimeClass)
    {
        nint module = 0;
        try
        {
            nint* moduleAddress = &module;
            int result = new ICorDebugClassAbi(runtimeClass).GetModule((nint)moduleAddress);
            module = Volatile.Read(ref *moduleAddress);
            CorDebugHResult.ThrowIfFailed(result, "ICorDebugClass.GetModule");
            uint token = 0;
            uint* tokenAddress = &token;
            CorDebugHResult.ThrowIfFailed(new ICorDebugClassAbi(runtimeClass).GetToken((nint)tokenAddress),
                "ICorDebugClass.GetToken");
            return (_moduleResolver(module) ?? throw new InvalidOperationException(
                "The continuation's runtime module is unavailable."), Volatile.Read(ref *tokenAddress));
        }
        finally
        {
            Release(module);
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
