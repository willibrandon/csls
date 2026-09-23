using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Csls.Debugger;

/// <summary>
/// Decodes CLR method signatures into stable function-evaluation type identities.
/// </summary>
internal sealed class FunctionEvaluationSignatureTypeProvider :
    ISignatureTypeProvider<string, object?>
{
    private const byte ValueTypeKind = 0x11;
    private const byte ClassTypeKind = 0x12;
    private const int MaximumNestingDepth = 256;
    private readonly ManagedMetadataImage? _metadata;
    private readonly int _specificationDepth;

    private FunctionEvaluationSignatureTypeProvider(ManagedMetadataImage? metadata, int specificationDepth)
    {
        _metadata = metadata;
        _specificationDepth = specificationDepth;
    }

    /// <summary>
    /// Creates a signature decoder over the current aggregate metadata generation.
    /// </summary>
    internal FunctionEvaluationSignatureTypeProvider(ManagedMetadataImage metadata) : this(metadata, 0)
    {
        ArgumentNullException.ThrowIfNull(metadata);
    }

    /// <summary>
    /// Gets the stateless signature type provider.
    /// </summary>
    internal static FunctionEvaluationSignatureTypeProvider Instance { get; } = new(null, 0);

    /// <inheritdoc />
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.TypedReference => "typed-reference",
        _ => throw new BadImageFormatException(
            $"Unsupported primitive signature type {typeCode}.")
    };

    /// <inheritdoc />
    public string GetTypeFromDefinition(
        MetadataReader reader,
        TypeDefinitionHandle handle,
        byte rawTypeKind) => AddTypeKind(
            GetTypeDefinitionName(reader, handle),
            rawTypeKind);

    /// <inheritdoc />
    public string GetTypeFromReference(
        MetadataReader reader,
        TypeReferenceHandle handle,
        byte rawTypeKind) => AddTypeKind(
            GetTypeReferenceName(reader, handle),
            rawTypeKind);

    /// <inheritdoc />
    public string GetTypeFromSpecification(
        MetadataReader reader,
        object? genericContext,
        TypeSpecificationHandle handle,
        byte rawTypeKind)
    {
        if (_specificationDepth == MaximumNestingDepth)
        {
            throw new BadImageFormatException("A function signature exceeds its type-specification nesting limit.");
        }

        var nested = new FunctionEvaluationSignatureTypeProvider(_metadata, _specificationDepth + 1);
        if (_metadata is null)
        {
            return AddTypeKind(reader.GetTypeSpecification(handle).DecodeSignature(nested, genericContext), rawTypeKind);
        }

        (MetadataReader owner, EntityHandle relative) = _metadata.Resolve(handle);
        BlobReader blob = _metadata.GetBlobReader(owner.GetTypeSpecification((TypeSpecificationHandle)relative).Signature);
        var decoder = new SignatureDecoder<string, object?>(nested, _metadata.Baseline, genericContext);
        return AddTypeKind(decoder.DecodeType(ref blob), rawTypeKind);
    }

    /// <inheritdoc />
    public string GetSZArrayType(string elementType) =>
        $"reference:{RemoveTypeKind(elementType)}[]";

    /// <inheritdoc />
    public string GetArrayType(string elementType, ArrayShape shape) =>
        $"reference:{RemoveTypeKind(elementType)}[{new string(',', shape.Rank - 1)}]";

    /// <inheritdoc />
    public string GetByReferenceType(string elementType) =>
        $"by-reference:{RemoveTypeKind(elementType)}";

    /// <inheritdoc />
    public string GetPointerType(string elementType) =>
        $"pointer:{RemoveTypeKind(elementType)}";

    /// <inheritdoc />
    public string GetGenericInstantiation(
        string genericType,
        ImmutableArray<string> typeArguments) =>
        $"{GetTypeKind(genericType)}{RemoveTypeKind(genericType)}" +
        $"<{string.Join(',', typeArguments.Select(RemoveTypeKind))}>";

    /// <inheritdoc />
    public string GetGenericMethodParameter(object? genericContext, int index) =>
        $"method-parameter:{index}";

    /// <inheritdoc />
    public string GetGenericTypeParameter(object? genericContext, int index) =>
        $"type-parameter:{index}";

    /// <inheritdoc />
    public string GetPinnedType(string elementType) => elementType;

    /// <inheritdoc />
    public string GetModifiedType(
        string modifier,
        string unmodifiedType,
        bool isRequired) => unmodifiedType;

    /// <inheritdoc />
    public string GetFunctionPointerType(MethodSignature<string> signature) =>
        "function-pointer";

    private string GetTypeDefinitionName(
        MetadataReader reader,
        TypeDefinitionHandle handle)
    {
        List<string> names = [];
        for (int depth = 0; depth < MaximumNestingDepth; depth++)
        {
            TypeDefinition type = _metadata is null ? reader.GetTypeDefinition(handle) : _metadata.GetTypeDefinition(handle);
            names.Add(GetString(reader, type.Name));
            TypeDefinitionHandle declaringType = _metadata is null ? type.GetDeclaringType() : _metadata.GetDeclaringType(handle);
            if (declaringType.IsNil)
            {
                return JoinTypeName(GetString(reader, type.Namespace), names);
            }

            handle = declaringType;
        }

        throw new BadImageFormatException("A function signature type exceeds its declaring-type nesting limit.");
    }

    private string GetTypeReferenceName(
        MetadataReader reader,
        TypeReferenceHandle handle)
    {
        List<string> names = [];
        for (int depth = 0; depth < MaximumNestingDepth; depth++)
        {
            TypeReference type = _metadata is null ? reader.GetTypeReference(handle) : _metadata.GetTypeReference(handle);
            names.Add(GetString(reader, type.Name));
            if (type.ResolutionScope.Kind != HandleKind.TypeReference)
            {
                return JoinTypeName(GetString(reader, type.Namespace), names);
            }

            handle = (TypeReferenceHandle)type.ResolutionScope;
        }

        throw new BadImageFormatException("A function signature type exceeds its reference nesting limit.");
    }

    private static string JoinTypeName(string @namespace, List<string> names)
    {
        names.Reverse();
        string name = string.Join('+', names);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }

    private string GetString(MetadataReader reader, StringHandle handle) =>
        _metadata is null ? reader.GetString(handle) : _metadata.GetString(handle);

    private static string AddTypeKind(string type, byte rawTypeKind) => rawTypeKind switch
    {
        ClassTypeKind => $"reference:{RemoveTypeKind(type)}",
        ValueTypeKind => $"value:{RemoveTypeKind(type)}",
        _ => type
    };

    private static string GetTypeKind(string type) => type.StartsWith(
        "reference:",
        StringComparison.Ordinal)
            ? "reference:"
            : type.StartsWith("value:", StringComparison.Ordinal)
                ? "value:"
                : string.Empty;

    private static string RemoveTypeKind(string type) =>
        type.StartsWith("reference:", StringComparison.Ordinal)
            ? type["reference:".Length..]
            : type.StartsWith("value:", StringComparison.Ordinal)
                ? type["value:".Length..]
                : type;
}
