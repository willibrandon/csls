using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Prepares a bounded value copy or default before mutating its original runtime storage.
/// </summary>
internal sealed class ManagedValueTypeAssignment : IDisposable
{
    private const uint MaximumCopyBytes = 1024 * 1024;
    private readonly byte[] _payload;
    private nint _destination;

    private ManagedValueTypeAssignment(nint destination, byte[] payload)
    {
        _destination = destination;
        _payload = payload;
    }

    /// <summary>
    /// Prepares zero-initialized primitive or struct storage without copying references from another scope.
    /// </summary>
    internal static ManagedValueTypeAssignment PrepareDefault(nint destination)
    {
        uint elementType = ManagedRuntimeValueIdentity.GetElementType(destination);
        if (elementType is not (>= 0x02 and <= 0x0d or 0x11 or 0x18 or 0x19))
        {
            throw new InvalidOperationException(
                "Default assignment requires primitive, value-type, or object-reference storage; " +
                "direct writes to managed by-reference and native pointer locations are not supported.");
        }

        uint size = GetSize(destination);
        if (size is 0 or > MaximumCopyBytes)
        {
            throw new InvalidOperationException(
                $"Default assignment requires a runtime size between 1 and {MaximumCopyBytes} bytes.");
        }

        byte[] payload = new byte[checked((int)size)];
        nint generic = ComAbi.QueryInterface(destination, ICorDebugGenericValueAbi.InterfaceId);
        try
        {
            var prepared = new ManagedValueTypeAssignment(generic, payload);
            generic = 0;
            return prepared;
        }
        finally
        {
            if (generic != 0)
            {
                _ = ComAbi.Release(generic);
            }
        }
    }

    /// <summary>
    /// Captures a complete same-type payload while both values belong to the current stopped operation.
    /// </summary>
    internal static unsafe ManagedValueTypeAssignment Prepare(
        nint destination,
        nint source,
        Func<nint, PEReader> openModule)
    {
        ArgumentNullException.ThrowIfNull(openModule);
        if (ManagedRuntimeValueIdentity.GetElementType(destination) != 0x11 ||
            ManagedRuntimeValueIdentity.GetElementType(source) != 0x11)
        {
            throw new InvalidOperationException(
                "Whole-value assignment requires existing unboxed value types; " +
                "implicit boxing and unboxing are not supported.");
        }

        if (!ManagedRuntimeValueIdentity.HaveSameRuntimeType(destination, source))
        {
            throw new InvalidOperationException(
                "Whole-value assignment requires identical loaded runtime types.");
        }

        ValidateDefinition(destination, openModule);
        uint destinationSize = GetSize(destination);
        uint sourceSize = GetSize(source);
        if (destinationSize != sourceSize || destinationSize is 0 or > MaximumCopyBytes)
        {
            throw new InvalidOperationException(
                $"Whole-value assignment requires equal runtime sizes between 1 and {MaximumCopyBytes} bytes.");
        }

        nint destinationGeneric = ComAbi.QueryInterface(destination, ICorDebugGenericValueAbi.InterfaceId);
        nint sourceGeneric = 0;
        try
        {
            sourceGeneric = ComAbi.QueryInterface(source, ICorDebugGenericValueAbi.InterfaceId);
            byte[] payload = GC.AllocateUninitializedArray<byte>(checked((int)destinationSize));
            fixed (byte* address = payload)
            {
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugGenericValueAbi(sourceGeneric).GetValue((nint)address),
                    "ICorDebugGenericValue.GetValue");
            }

            var prepared = new ManagedValueTypeAssignment(destinationGeneric, payload);
            destinationGeneric = 0;
            return prepared;
        }
        finally
        {
            if (sourceGeneric != 0)
            {
                _ = ComAbi.Release(sourceGeneric);
            }

            if (destinationGeneric != 0)
            {
                _ = ComAbi.Release(destinationGeneric);
            }
        }
    }

    /// <summary>
    /// Rejects member writes whose parent has no addressable value-type storage.
    /// </summary>
    internal static unsafe void ValidateFieldParent(nint parent)
    {
        if (ManagedRuntimeValueIdentity.GetElementType(parent) != 0x11)
        {
            return;
        }

        ulong address = 0;
        ulong* addressPointer = &address;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugValueAbi(parent).GetAddress((nint)addressPointer),
            "ICorDebugValue.GetAddress");
        if (Volatile.Read(ref *addressPointer) == 0)
        {
            throw new InvalidOperationException(
                "Member assignment requires addressable value-type storage; " +
                "writing individual fields of register-backed values is not supported.");
        }
    }

    /// <summary>
    /// Writes the prepared payload through the runtime's GC-aware value home without executing target code.
    /// </summary>
    internal unsafe void Write()
    {
        ObjectDisposedException.ThrowIf(_destination == 0, this);
        fixed (byte* address = _payload)
        {
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugGenericValueAbi(_destination).SetValue((nint)address),
                "ICorDebugGenericValue.SetValue");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        nint destination = Interlocked.Exchange(ref _destination, 0);
        if (destination != 0)
        {
            _ = ComAbi.Release(destination);
        }
    }

    private static unsafe uint GetSize(nint value)
    {
        uint size = 0;
        uint* sizePointer = &size;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugValueAbi(value).GetSize((nint)sizePointer),
            "ICorDebugValue.GetSize");
        return Volatile.Read(ref *sizePointer);
    }

    private static unsafe void ValidateDefinition(nint value, Func<nint, PEReader> openModule)
    {
        nint instance = ComAbi.QueryInterface(value, ICorDebugObjectValueAbi.InterfaceId);
        nint runtimeClass = 0;
        nint module = 0;
        try
        {
            nint* classPointer = &runtimeClass;
            int classResult = new ICorDebugObjectValueAbi(instance).GetClass((nint)classPointer);
            runtimeClass = Volatile.Read(ref *classPointer);
            CorDebugHResult.ThrowIfFailed(classResult, "ICorDebugObjectValue.GetClass");
            if (runtimeClass == 0)
            {
                throw new InvalidOperationException("The value type has no loaded runtime class.");
            }

            nint* modulePointer = &module;
            int moduleResult = new ICorDebugClassAbi(runtimeClass).GetModule((nint)modulePointer);
            module = Volatile.Read(ref *modulePointer);
            CorDebugHResult.ThrowIfFailed(moduleResult, "ICorDebugClass.GetModule");
            if (module == 0)
            {
                throw new InvalidOperationException("The value type has no loaded runtime module.");
            }

            uint token = 0;
            uint* tokenPointer = &token;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugClassAbi(runtimeClass).GetToken((nint)tokenPointer),
                "ICorDebugClass.GetToken");
            EntityHandle entity = MetadataTokens.EntityHandle(checked((int)Volatile.Read(ref *tokenPointer)));
            if (entity.Kind != HandleKind.TypeDefinition)
            {
                throw new BadImageFormatException("The value type has no metadata type definition.");
            }

            using PEReader peReader = openModule(module);
            MetadataReader metadata = peReader.GetMetadataReader();
            TypeDefinition definition = metadata.GetTypeDefinition((TypeDefinitionHandle)entity);
            if (definition.GetCustomAttributes().Any(attributeHandle =>
                ManagedDebuggerAttributeReader.GetAttributeTypeName(
                    metadata, metadata.GetCustomAttribute(attributeHandle)) ==
                    "System.Runtime.CompilerServices.IsByRefLikeAttribute"))
            {
                throw new InvalidOperationException(
                    "Whole-value assignment of ref-like types is not supported because " +
                    "their referenced storage lifetimes cannot be established.");
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

            _ = ComAbi.Release(instance);
        }
    }
}
