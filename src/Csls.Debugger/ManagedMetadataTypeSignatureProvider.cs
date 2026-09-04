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

    /// <summary>
    /// Creates a signature provider preserving its borrowed source runtime module.
    /// </summary>
    /// <param name="sourceModule">The borrowed runtime module, or zero for metadata-only decoding.</param>
    internal ManagedMetadataTypeSignatureProvider(nint sourceModule)
    {
        _sourceModule = sourceModule;
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
        ManagedMetadataTypeSignature elementType) => throw Unsupported("by-reference");

    /// <inheritdoc />
    public ManagedMetadataTypeSignature GetFunctionPointerType(
        MethodSignature<ManagedMetadataTypeSignature> signature) =>
        throw Unsupported("function-pointer");

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
        int index) => throw Unsupported("generic method parameter");

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
        ManagedMetadataTypeSignature elementType) => throw Unsupported("pointer");

    /// <inheritdoc />
    public ManagedMetadataTypeSignature GetPrimitiveType(PrimitiveTypeCode typeCode) =>
        typeCode switch
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
        byte rawTypeKind) => reader.GetTypeSpecification(handle)
        .DecodeSignature(this, genericContext);

    private static ManagedMetadataTypeSignature Primitive(
        string metadataName,
        bool isValueType) => new(
            metadataName,
            AssemblyName: null,
            GenericTypeParameterIndex: null,
            TypeArguments: [],
            ArrayShapes: [],
            isValueType);

    private static string GetTypeDefinitionName(
        MetadataReader reader,
        TypeDefinitionHandle handle)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        string name = reader.GetString(type.Name);
        TypeDefinitionHandle declaringType = type.GetDeclaringType();
        if (!declaringType.IsNil)
        {
            return $"{GetTypeDefinitionName(reader, declaringType)}+{name}";
        }

        string typeNamespace = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(typeNamespace) ? name : $"{typeNamespace}.{name}";
    }

    private static string GetTypeReferenceName(
        MetadataReader reader,
        TypeReferenceHandle handle)
    {
        TypeReference type = reader.GetTypeReference(handle);
        string name = reader.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return $"{GetTypeReferenceName(
                reader,
                (TypeReferenceHandle)type.ResolutionScope)}+{name}";
        }

        string typeNamespace = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(typeNamespace) ? name : $"{typeNamespace}.{name}";
    }

    private static string? GetTypeReferenceAssemblyName(
        MetadataReader reader,
        TypeReferenceHandle handle)
    {
        EntityHandle scope = reader.GetTypeReference(handle).ResolutionScope;
        for (int depth = 0; depth < 256; depth++)
        {
            if (scope.Kind == HandleKind.AssemblyReference)
            {
                return reader.GetString(
                    reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
            }

            if (scope.Kind != HandleKind.TypeReference)
            {
                return GetCurrentAssemblyName(reader);
            }

            scope = reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        }

        throw new BadImageFormatException("A metadata type reference exceeds 256 nested levels.");
    }

    private static uint GetAssemblyReferenceToken(
        MetadataReader reader,
        TypeReferenceHandle handle)
    {
        EntityHandle scope = reader.GetTypeReference(handle).ResolutionScope;
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

            scope = reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        }

        throw new BadImageFormatException("A metadata type reference exceeds 256 nested levels.");
    }

    private static string? GetCurrentAssemblyName(MetadataReader reader) => reader.IsAssembly
        ? reader.GetString(reader.GetAssemblyDefinition().Name)
        : null;

    private static BadImageFormatException Unsupported(string kind) => new(
        $"Results View does not support a {kind} interface signature.");
}
