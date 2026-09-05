using Csls.Debugger.Interop;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Binds metadata and runtime types to exact immutable identities for stopped-state semantic checks.
/// </summary>
internal sealed class ManagedBoundTypeSystem
{
    private const int MaximumDepth = 128;
    private readonly SourceBreakpointManager _modules;
    private readonly ManagedRuntimeTypeCatalog _catalog;
    private readonly ManagedCoreLibrary _coreLibrary;

    /// <summary>
    /// Creates a type system scoped to one debuggee's loaded modules.
    /// </summary>
    internal ManagedBoundTypeSystem(SourceBreakpointManager modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _modules = modules;
        _catalog = new ManagedRuntimeTypeCatalog(modules);
        _coreLibrary = new ManagedCoreLibrary(modules);
    }

    /// <summary>
    /// Binds a declaration while substituting its exact containing-type and method arguments.
    /// </summary>
    internal ManagedBoundType Bind(
        ManagedMetadataTypeSignature signature,
        IReadOnlyList<ManagedBoundType> typeArguments,
        IReadOnlyList<ManagedBoundType> methodArguments,
        nint thread) => BindCore(signature, typeArguments, methodArguments, thread, depth: 0);

    /// <summary>
    /// Captures a value's actual runtime type without relying on its formatted display name.
    /// </summary>
    internal unsafe ManagedBoundType CaptureValue(nint value, nint thread)
    {
        nint value2 = ComAbi.QueryInterface(value, ICorDebugValue2Abi.InterfaceId);
        nint type = 0;
        try
        {
            nint* address = &type;
            CorDebugHResult.ThrowIfFailed(new ICorDebugValue2Abi(value2).GetExactType((nint)address),
                "ICorDebugValue2.GetExactType");
            type = RequirePointer(Volatile.Read(ref *address));
            return CaptureType(type, thread);
        }
        finally
        {
            Release(type);
            Release(value2);
        }
    }

    /// <summary>
    /// Captures a borrowed exact runtime type and releases all temporary native references.
    /// </summary>
    internal ManagedBoundType CaptureType(nint type, nint thread) => CaptureTypeCore(type, thread, depth: 0);

    /// <summary>
    /// Binds a call's declared result before execution can invalidate its borrowed runtime context.
    /// </summary>
    internal ManagedBoundType? BindMethodResult(
        nint modulePointer, uint methodToken, IReadOnlyList<ManagedBoundType> typeArguments,
        nint thread, bool constructsObject = false)
    {
        CorDebugLoadedModule module = _modules.FindModule(modulePointer)
            ?? throw new InvalidOperationException("The method's runtime module is unavailable.");
        using PEReader pe = OpenModule(module);
        MetadataReader reader = pe.GetMetadataReader();
        using var metadata = new ManagedMetadataImage(reader, module.MetadataDeltas);
        var method = (MethodDefinitionHandle)MetadataTokens.EntityHandle(checked((int)methodToken));
        var provider = new ManagedMetadataTypeSignatureProvider(module.Pointer, metadata);
        if (constructsObject)
        {
            TypeDefinitionHandle handle = metadata.GetDeclaringType(method);
            TypeDefinition declaration = metadata.GetTypeDefinition(handle);
            ManagedBoundType? parent = declaration.BaseType.IsNil ? null
                : Bind(DecodeType(provider, reader, declaration.BaseType), typeArguments, [], thread);
            bool isValueType = parent is not null && parent.ModuleId == _coreLibrary.GetModule(thread).Id &&
                parent.Name is "System.ValueType" or "System.Enum";
            return CreateDefinition(module, checked((uint)MetadataTokens.GetToken(handle)),
                isValueType ? 0x11U : 0x12U, typeArguments, thread);
        }

        ManagedMetadataTypeSignature signature = metadata.DecodeMethodSignature(method, module.Pointer).ReturnType;
        return signature.PrimitiveType == PrimitiveTypeCode.Void ? null
            : Bind(signature, typeArguments, [], thread);
    }

    /// <summary>
    /// Gets immediate base and interface declarations with their closed argument substitutions.
    /// </summary>
    internal IReadOnlyList<ManagedBoundType> GetParents(ManagedBoundType type, nint thread)
    {
        if (type.IsArray)
        {
            return [BindCoreLibraryType("System.Array", [], thread)];
        }

        CorDebugLoadedModule module = GetModule(type);
        using PEReader pe = OpenModule(module);
        MetadataReader reader = pe.GetMetadataReader();
        TypeDefinition definition = GetDefinition(reader, type.DefinitionToken);
        var provider = new ManagedMetadataTypeSignatureProvider(module.Pointer);
        List<ManagedBoundType> result = [];
        if (!definition.BaseType.IsNil)
        {
            result.Add(Bind(DecodeType(provider, reader, definition.BaseType), type.TypeArguments, [], thread));
        }

        foreach (InterfaceImplementationHandle handle in definition.GetInterfaceImplementations())
        {
            EntityHandle interfaceType = reader.GetInterfaceImplementation(handle).Interface;
            result.Add(Bind(DecodeType(provider, reader, interfaceType), type.TypeArguments, [], thread));
        }

        return result;
    }

    /// <summary>
    /// Gets the declaration's variance flags in generic parameter order.
    /// </summary>
    internal IReadOnlyList<GenericParameterAttributes> GetVariance(ManagedBoundType type)
    {
        using PEReader pe = OpenModule(GetModule(type));
        MetadataReader reader = pe.GetMetadataReader();
        return [.. GetDefinition(reader, type.DefinitionToken).GetGenericParameters()
            .Select(handle => reader.GetGenericParameter(handle).Attributes & GenericParameterAttributes.VarianceMask)];
    }

    /// <summary>
    /// Identifies the runtime's special generic interfaces implemented by vector arrays.
    /// </summary>
    internal bool IsVectorInterface(ManagedBoundType type, nint thread) =>
        type.ModuleId == _coreLibrary.GetModule(thread).Id && type.TypeArguments.Count == 1 &&
        type.Name is "System.Collections.Generic.IEnumerable`1" or
            "System.Collections.Generic.ICollection`1" or "System.Collections.Generic.IList`1" or
            "System.Collections.Generic.IReadOnlyCollection`1" or "System.Collections.Generic.IReadOnlyList`1";

    /// <summary>
    /// Binds a field signature using the actual closed type that declares the field.
    /// </summary>
    internal ManagedBoundType BindField(nint declaringType, nint modulePointer, uint fieldToken, nint thread)
    {
        CorDebugLoadedModule module = _modules.FindModule(modulePointer)
            ?? throw new InvalidOperationException("The field's runtime module is unavailable.");
        using PEReader pe = OpenModule(module);
        MetadataReader reader = pe.GetMetadataReader();
        FieldDefinition definition = reader.GetFieldDefinition(
            MetadataTokens.FieldDefinitionHandle(checked((int)(fieldToken & 0x00ffffff))));
        ManagedMetadataTypeSignature signature = definition.DecodeSignature(
            new ManagedMetadataTypeSignatureProvider(module.Pointer), genericContext: null);
        return Bind(signature, CaptureType(declaringType, thread).TypeArguments, [], thread);
    }

    private ManagedBoundType BindCore(
        ManagedMetadataTypeSignature signature,
        IReadOnlyList<ManagedBoundType> typeArguments,
        IReadOnlyList<ManagedBoundType> methodArguments,
        nint thread,
        int depth)
    {
        CheckDepth(depth);
        if (signature.UnsupportedKind is string kind)
        {
            throw new InvalidOperationException($"A {kind} signature is not an ordinary assignment type.");
        }

        ManagedBoundType result;
        int? parameterIndex = signature.GenericTypeParameterIndex ?? signature.GenericMethodParameterIndex;
        if (parameterIndex is int index)
        {
            IReadOnlyList<ManagedBoundType> arguments = signature.GenericMethodParameterIndex is null
                ? typeArguments : methodArguments;
            if ((uint)index >= (uint)arguments.Count ||
                (signature.GenericMethodParameterIndex is not null && signature.GenericTypeParameterIndex is not null))
            {
                throw new BadImageFormatException("A generic parameter is outside its declaring scope.");
            }

            result = arguments[index];
        }
        else
        {
            if (signature.PrimitiveType is not null)
            {
                signature = _coreLibrary.Resolve(signature, thread);
            }

            if (!_catalog.TryResolveSignature(signature, out CorDebugLoadedModule? module, out uint token) || module is null)
            {
                throw new InvalidOperationException($"Declared type '{signature.MetadataName}' is not loaded uniquely.");
            }

            ManagedBoundType[] arguments = [.. signature.TypeArguments.Select(
                argument => BindCore(argument, typeArguments, methodArguments, thread, depth + 1))];
            result = CreateDefinition(module, token, signature.IsValueType ? 0x11U : 0x12U, arguments, thread);
        }

        foreach (ManagedMetadataArrayShape shape in signature.ArrayShapes)
        {
            if (shape.Rank is < 1 or > 32 || (shape.IsVector && shape.Rank != 1))
            {
                throw new BadImageFormatException("An array signature has an invalid rank.");
            }

            result = CreateArray(result, shape.IsVector ? 0x1dU : 0x14U, shape.Rank);
        }

        return result;
    }

    private unsafe ManagedBoundType CaptureTypeCore(nint type, nint thread, int depth)
    {
        CheckDepth(depth);
        uint kind = 0;
        uint* kindAddress = &kind;
        CorDebugHResult.ThrowIfFailed(new ICorDebugTypeAbi(type).GetType((nint)kindAddress), "ICorDebugType.GetType");
        kind = Volatile.Read(ref *kindAddress);
        if (kind is 0x14 or 0x1d)
        {
            nint element = 0;
            try
            {
                nint* elementAddress = &element;
                CorDebugHResult.ThrowIfFailed(new ICorDebugTypeAbi(type).GetFirstTypeParameter((nint)elementAddress),
                    "ICorDebugType.GetFirstTypeParameter");
                element = RequirePointer(Volatile.Read(ref *elementAddress));
                uint rank = 0;
                uint* rankAddress = &rank;
                CorDebugHResult.ThrowIfFailed(new ICorDebugTypeAbi(type).GetRank((nint)rankAddress), "ICorDebugType.GetRank");
                return CreateArray(CaptureTypeCore(element, thread, depth + 1), kind, checked((int)Volatile.Read(ref *rankAddress)));
            }
            finally
            {
                Release(element);
            }
        }

        if (kind is not (0x11 or 0x12))
        {
            ManagedMetadataTypeSignature primitive = ManagedMetadataTypeSignatureProvider.Instance
                .GetPrimitiveType((PrimitiveTypeCode)kind);
            return Bind(primitive, [], [], thread);
        }

        nint runtimeClass = 0;
        nint modulePointer = 0;
        nint[] arguments = [];
        try
        {
            nint* classAddress = &runtimeClass;
            CorDebugHResult.ThrowIfFailed(new ICorDebugTypeAbi(type).GetClass((nint)classAddress), "ICorDebugType.GetClass");
            runtimeClass = RequirePointer(Volatile.Read(ref *classAddress));
            nint* moduleAddress = &modulePointer;
            CorDebugHResult.ThrowIfFailed(new ICorDebugClassAbi(runtimeClass).GetModule((nint)moduleAddress),
                "ICorDebugClass.GetModule");
            modulePointer = RequirePointer(Volatile.Read(ref *moduleAddress));
            uint token = 0;
            uint* tokenAddress = &token;
            CorDebugHResult.ThrowIfFailed(new ICorDebugClassAbi(runtimeClass).GetToken((nint)tokenAddress),
                "ICorDebugClass.GetToken");
            CorDebugLoadedModule module = _modules.FindModule(modulePointer)
                ?? throw new InvalidOperationException("The type's runtime module is unavailable.");
            arguments = ManagedRuntimeTypeArguments.Retain(type);
            ManagedBoundType[] boundArguments = [.. arguments.Select(argument => CaptureTypeCore(argument, thread, depth + 1))];
            return CreateDefinition(module, Volatile.Read(ref *tokenAddress), kind, boundArguments, thread);
        }
        finally
        {
            foreach (nint argument in arguments)
            {
                Release(argument);
            }

            Release(modulePointer);
            Release(runtimeClass);
        }
    }

    private ManagedBoundType CreateDefinition(
        CorDebugLoadedModule module, uint token, uint kind, IReadOnlyList<ManagedBoundType> arguments, nint thread)
    {
        using PEReader pe = OpenModule(module);
        MetadataReader reader = pe.GetMetadataReader();
        ManagedMetadataTypeSignature signature = new ManagedMetadataTypeSignatureProvider(module.Pointer)
            .GetTypeFromDefinition(reader, (TypeDefinitionHandle)MetadataTokens.EntityHandle(checked((int)token)), (byte)kind);
        string name = signature.MetadataName ?? throw new BadImageFormatException("A type has no metadata name.");
        if (module.Id == _coreLibrary.GetModule(thread).Id)
        {
            kind = name switch
            {
                "System.Boolean" => 0x02,
                "System.Char" => 0x03,
                "System.SByte" => 0x04,
                "System.Byte" => 0x05,
                "System.Int16" => 0x06,
                "System.UInt16" => 0x07,
                "System.Int32" => 0x08,
                "System.UInt32" => 0x09,
                "System.Int64" => 0x0a,
                "System.UInt64" => 0x0b,
                "System.Single" => 0x0c,
                "System.Double" => 0x0d,
                "System.String" => 0x0e,
                "System.TypedReference" => 0x16,
                "System.IntPtr" => 0x18,
                "System.UIntPtr" => 0x19,
                "System.Object" => 0x1c,
                "System.Void" => 0x01,
                _ => kind
            };
        }

        return new ManagedBoundType(kind, module.Id, token, name, arguments);
    }

    private ManagedBoundType BindCoreLibraryType(string name, IReadOnlyList<ManagedBoundType> arguments, nint thread)
    {
        ManagedMetadataTypeSignature signature = _coreLibrary.Resolve(new ManagedMetadataTypeSignature(
            name, null, null, [], [], IsValueType: false), thread);
        CorDebugLoadedModule module = _coreLibrary.GetModule(thread);
        return CreateDefinition(module, signature.DefinitionToken, 0x12, arguments, thread);
    }

    private CorDebugLoadedModule GetModule(ManagedBoundType type) => type.ModuleId is int id
        ? _modules.FindModule(id) ?? throw new InvalidOperationException("The declared type's module has unloaded.")
        : throw new InvalidOperationException("The type has no defining module.");

    private static PEReader OpenModule(CorDebugLoadedModule module) => module.OpenPeReader()
        ?? throw new InvalidOperationException("The declared type's metadata is unavailable.");

    private static TypeDefinition GetDefinition(MetadataReader reader, uint token) => reader.GetTypeDefinition(
        (TypeDefinitionHandle)MetadataTokens.EntityHandle(checked((int)token)));

    private static ManagedMetadataTypeSignature DecodeType(
        ManagedMetadataTypeSignatureProvider provider, MetadataReader reader, EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => provider.GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0x12),
            HandleKind.TypeReference => provider.GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0x12),
            HandleKind.TypeSpecification => provider.GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)handle, 0x12),
            _ => throw new BadImageFormatException("A type hierarchy entry is not a type token.")
        };

    private static ManagedBoundType CreateArray(ManagedBoundType element, uint kind, int rank) => new(
        kind, null, 0, $"{element.Name}[{(kind == 0x1d ? string.Empty : rank == 1 ? "*" : new string(',', rank - 1))}]",
        [element], rank);

    private static void CheckDepth(int depth)
    {
        if (depth >= MaximumDepth)
        {
            throw new BadImageFormatException($"A declared type exceeds {MaximumDepth} nested levels.");
        }
    }

    private static nint RequirePointer(nint pointer) => pointer != 0
        ? pointer : throw new InvalidOperationException("The runtime returned no type reference.");

    private static void Release(nint pointer)
    {
        if (pointer != 0)
        {
            _ = ComAbi.Release(pointer);
        }
    }
}
