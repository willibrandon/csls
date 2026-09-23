using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Csls.Debugger;

/// <summary>
/// Decodes metadata type signatures while preserving type-parameter substitutions.
/// </summary>
internal sealed class ManagedMetadataTypeSignatureProvider :
    ISignatureTypeProvider<ManagedMetadataTypeSignature, object?>
{
    private const byte ValueTypeKind = 0x11;
    private readonly nint _sourceModule;
    private readonly ManagedMetadataImage? _metadata;

    /// <summary>
    /// Creates a signature provider preserving its borrowed source runtime module.
    /// </summary>
    /// <param name="sourceModule">The borrowed runtime module, or zero for metadata-only decoding.</param>
    /// <param name="metadata">The scoped aggregate reader when decoding a Hot Reload metadata chain.</param>
    internal ManagedMetadataTypeSignatureProvider(nint sourceModule, ManagedMetadataImage? metadata = null)
    {
        _sourceModule = sourceModule;
        _metadata = metadata;
    }

    /// <summary>
    /// Gets the stateless metadata signature provider.
    /// </summary>
    internal static ManagedMetadataTypeSignatureProvider Instance { get; } = new(0);

    /// <inheritdoc />
    public ManagedMetadataTypeSignature GetArrayType(
        ManagedMetadataTypeSignature elementType,
        ArrayShape shape) => elementType with
        {
            ArrayShapes = [.. elementType.ArrayShapes, new(shape.Rank, IsVector: false)]
        };

    /// <inheritdoc />
    public ManagedMetadataTypeSignature GetByReferenceType(
        ManagedMetadataTypeSignature elementType) => elementType with { UnsupportedKind = "by-reference" };

    /// <inheritdoc />
    public ManagedMetadataTypeSignature GetFunctionPointerType(
        MethodSignature<ManagedMetadataTypeSignature> signature) => new(
            MetadataName: null, AssemblyName: null, GenericTypeParameterIndex: null,
            TypeArguments: [], ArrayShapes: [], IsValueType: false, UnsupportedKind: "function-pointer");

    /// <inheritdoc />
    public ManagedMetadataTypeSignature GetGenericInstantiation(
        ManagedMetadataTypeSignature genericType,
        ImmutableArray<ManagedMetadataTypeSignature> typeArguments) => genericType with
        {
            TypeArguments = typeArguments
        };

    /// <inheritdoc />
    public ManagedMetadataTypeSignature GetGenericMethodParameter(
        object? genericContext,
        int index) => new(
            MetadataName: null, AssemblyName: null, GenericTypeParameterIndex: null,
            TypeArguments: [], ArrayShapes: [], IsValueType: false, GenericMethodParameterIndex: index);

    /// <inheritdoc />
    public ManagedMetadataTypeSignature GetGenericTypeParameter(
        object? genericContext,
        int index) => new(
            MetadataName: null,
            AssemblyName: null,
            GenericTypeParameterIndex: index,
            TypeArguments: [],
            ArrayShapes: [],
            IsValueType: false);

    /// <inheritdoc />
    public ManagedMetadataTypeSignature GetModifiedType(
        ManagedMetadataTypeSignature modifier,
        ManagedMetadataTypeSignature unmodifiedType,
        bool isRequired) => unmodifiedType;

    /// <inheritdoc />
    public ManagedMetadataTypeSignature GetPinnedType(
        ManagedMetadataTypeSignature elementType) => elementType;

    /// <inheritdoc />
    public ManagedMetadataTypeSignature GetPointerType(
        ManagedMetadataTypeSignature elementType) => elementType with { UnsupportedKind = "pointer" };

    /// <inheritdoc />
    public ManagedMetadataTypeSignature GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
        ManagedMetadataTypeSignature signature = typeCode switch
        {
            PrimitiveTypeCode.Void => Primitive("System.Void", isValueType: true),
            PrimitiveTypeCode.Boolean => Primitive("System.Boolean", isValueType: true),
            PrimitiveTypeCode.Byte => Primitive("System.Byte", isValueType: true),
            PrimitiveTypeCode.Char => Primitive("System.Char", isValueType: true),
            PrimitiveTypeCode.Double => Primitive("System.Double", isValueType: true),
            PrimitiveTypeCode.Int16 => Primitive("System.Int16", isValueType: true),
            PrimitiveTypeCode.Int32 => Primitive("System.Int32", isValueType: true),
            PrimitiveTypeCode.Int64 => Primitive("System.Int64", isValueType: true),
            PrimitiveTypeCode.IntPtr => Primitive("System.IntPtr", isValueType: true),
            PrimitiveTypeCode.Object => Primitive("System.Object", isValueType: false),
            PrimitiveTypeCode.SByte => Primitive("System.SByte", isValueType: true),
            PrimitiveTypeCode.Single => Primitive("System.Single", isValueType: true),
            PrimitiveTypeCode.String => Primitive("System.String", isValueType: false),
            PrimitiveTypeCode.TypedReference =>
                Primitive("System.TypedReference", isValueType: true),
            PrimitiveTypeCode.UInt16 => Primitive("System.UInt16", isValueType: true),
            PrimitiveTypeCode.UInt32 => Primitive("System.UInt32", isValueType: true),
            PrimitiveTypeCode.UInt64 => Primitive("System.UInt64", isValueType: true),
            PrimitiveTypeCode.UIntPtr => Primitive("System.UIntPtr", isValueType: true),
            _ => throw Unsupported($"primitive {typeCode}")
        };
        return signature with { PrimitiveType = typeCode };
    }

    /// <inheritdoc />
    public ManagedMetadataTypeSignature GetSZArrayType(
        ManagedMetadataTypeSignature elementType) => elementType with
        {
            ArrayShapes = [.. elementType.ArrayShapes, new(1, IsVector: true)]
        };

    /// <inheritdoc />
    public ManagedMetadataTypeSignature GetTypeFromDefinition(
        MetadataReader reader,
        TypeDefinitionHandle handle,
        byte rawTypeKind) => new(
            GetTypeDefinitionName(reader, handle),
            GetCurrentAssemblyName(reader),
            GenericTypeParameterIndex: null,
            TypeArguments: [],
            ArrayShapes: [],
            IsValueType: rawTypeKind == ValueTypeKind,
            SourceModule: _sourceModule,
            DefinitionToken: checked((uint)MetadataTokens.GetToken(handle)));

    /// <inheritdoc />
    public ManagedMetadataTypeSignature GetTypeFromReference(
        MetadataReader reader,
        TypeReferenceHandle handle,
        byte rawTypeKind) => new(
            GetTypeReferenceName(reader, handle),
            GetTypeReferenceAssemblyName(reader, handle),
            GenericTypeParameterIndex: null,
            TypeArguments: [],
            ArrayShapes: [],
            IsValueType: rawTypeKind == ValueTypeKind,
            SourceModule: _sourceModule,
            AssemblyReferenceToken: GetAssemblyReferenceToken(reader, handle));

    /// <inheritdoc />
    public ManagedMetadataTypeSignature GetTypeFromSpecification(
        MetadataReader reader,
        object? genericContext,
        TypeSpecificationHandle handle,
        byte rawTypeKind)
    {
        if (_metadata is null)
        {
            return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        }

        (MetadataReader owner, EntityHandle relative) = _metadata.Resolve(handle);
        BlobReader blob = _metadata.GetBlobReader(owner.GetTypeSpecification((TypeSpecificationHandle)relative).Signature);
        var decoder = new SignatureDecoder<ManagedMetadataTypeSignature, object?>(this, _metadata.Baseline, genericContext);
        return decoder.DecodeType(ref blob);
    }

    private static ManagedMetadataTypeSignature Primitive(
        string metadataName,
        bool isValueType) => new(
            metadataName,
            AssemblyName: null,
            GenericTypeParameterIndex: null,
            TypeArguments: [],
            ArrayShapes: [],
            isValueType);

    private string GetTypeDefinitionName(
        MetadataReader reader,
        TypeDefinitionHandle handle)
    {
        TypeDefinition type = _metadata is null ? reader.GetTypeDefinition(handle) : _metadata.GetTypeDefinition(handle);
        string name = GetString(reader, type.Name);
        TypeDefinitionHandle declaringType = _metadata is null ? type.GetDeclaringType() : _metadata.GetDeclaringType(handle);
        if (!declaringType.IsNil)
        {
            return $"{GetTypeDefinitionName(reader, declaringType)}+{name}";
        }

        string typeNamespace = GetString(reader, type.Namespace);
        return string.IsNullOrEmpty(typeNamespace) ? name : $"{typeNamespace}.{name}";
    }

    private string GetTypeReferenceName(
        MetadataReader reader,
        TypeReferenceHandle handle)
    {
        TypeReference type = GetTypeReference(reader, handle);
        string name = GetString(reader, type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return $"{GetTypeReferenceName(
                reader,
                (TypeReferenceHandle)type.ResolutionScope)}+{name}";
        }

        string typeNamespace = GetString(reader, type.Namespace);
        return string.IsNullOrEmpty(typeNamespace) ? name : $"{typeNamespace}.{name}";
    }

    private string? GetTypeReferenceAssemblyName(
        MetadataReader reader,
        TypeReferenceHandle handle)
    {
        EntityHandle scope = GetTypeReference(reader, handle).ResolutionScope;
        for (int depth = 0; depth < 256; depth++)
        {
            if (scope.Kind == HandleKind.AssemblyReference)
            {
                AssemblyReference assembly = _metadata is null
                    ? reader.GetAssemblyReference((AssemblyReferenceHandle)scope)
                    : _metadata.GetAssemblyReference((AssemblyReferenceHandle)scope);
                return GetString(reader, assembly.Name);
            }

            if (scope.Kind != HandleKind.TypeReference)
            {
                return GetCurrentAssemblyName(reader);
            }

            scope = GetTypeReference(reader, (TypeReferenceHandle)scope).ResolutionScope;
        }

        throw new BadImageFormatException("A metadata type reference exceeds 256 nested levels.");
    }

    private uint GetAssemblyReferenceToken(
        MetadataReader reader,
        TypeReferenceHandle handle)
    {
        EntityHandle scope = GetTypeReference(reader, handle).ResolutionScope;
        for (int depth = 0; depth < 256; depth++)
        {
            if (scope.Kind == HandleKind.AssemblyReference)
            {
                return checked((uint)MetadataTokens.GetToken(scope));
            }

            if (scope.Kind != HandleKind.TypeReference)
            {
                return 0;
            }

            scope = GetTypeReference(reader, (TypeReferenceHandle)scope).ResolutionScope;
        }

        throw new BadImageFormatException("A metadata type reference exceeds 256 nested levels.");
    }

    private string? GetCurrentAssemblyName(MetadataReader reader) => (_metadata?.Baseline ?? reader).IsAssembly
        ? GetString(reader, (_metadata?.Baseline ?? reader).GetAssemblyDefinition().Name)
        : null;

    private string GetString(MetadataReader reader, StringHandle handle) =>
        _metadata is null ? reader.GetString(handle) : _metadata.GetString(handle);

    private TypeReference GetTypeReference(MetadataReader reader, TypeReferenceHandle handle) =>
        _metadata is null ? reader.GetTypeReference(handle) : _metadata.GetTypeReference(handle);

    private static BadImageFormatException Unsupported(string kind) => new(
        $"The debugger does not support a {kind} metadata signature.");
}
