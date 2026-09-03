using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace Csls.Debugger;

/// <summary>
/// Decodes CLR method signatures into stable function-evaluation type identities.
/// </summary>
internal sealed class FunctionEvaluationSignatureTypeProvider :
    ISignatureTypeProvider<string, object?>
{
    private const byte ValueTypeKind = 0x11;
    private const byte ClassTypeKind = 0x12;

    private FunctionEvaluationSignatureTypeProvider()
    {
    }

    /// <summary>
    /// Gets the stateless signature type provider.
    /// </summary>
    internal static FunctionEvaluationSignatureTypeProvider Instance { get; } = new();

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
        byte rawTypeKind) => AddTypeKind(
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext),
            rawTypeKind);

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

        string @namespace = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
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

        string @namespace = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }

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
