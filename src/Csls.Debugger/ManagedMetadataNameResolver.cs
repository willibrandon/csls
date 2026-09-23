using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Csls.Debugger;

/// <summary>
/// Resolves IL metadata operands without loading or executing target assemblies.
/// </summary>
internal static class ManagedMetadataNameResolver
{
    /// <summary>
    /// Resolves a metadata token to a compact language-neutral display.
    /// </summary>
    /// <param name="reader">The containing module metadata.</param>
    /// <param name="token">The encoded metadata token.</param>
    /// <returns>The resolved display, or null when the token has no safe name.</returns>
    internal static string? Resolve(MetadataReader reader, int token)
    {
        try
        {
            Handle handle = MetadataTokens.Handle(token);
            return handle.Kind switch
            {
                HandleKind.TypeDefinition => TypeDefinitionName(
                    reader,
                    (TypeDefinitionHandle)handle),
                HandleKind.TypeReference => TypeReferenceName(
                    reader,
                    (TypeReferenceHandle)handle),
                HandleKind.MethodDefinition => MethodDefinitionName(
                    reader,
                    (MethodDefinitionHandle)handle),
                HandleKind.FieldDefinition => FieldDefinitionName(
                    reader,
                    (FieldDefinitionHandle)handle),
                HandleKind.MemberReference => MemberReferenceName(
                    reader,
                    (MemberReferenceHandle)handle),
                HandleKind.MethodSpecification => MethodSpecificationName(
                    reader,
                    (MethodSpecificationHandle)handle),
                HandleKind.UserString => UserString(reader, (UserStringHandle)handle),
                HandleKind.StandaloneSignature => "signature",
                _ => null
            };
        }
        catch (Exception exception) when (
            exception is ArgumentException or BadImageFormatException)
        {
            return null;
        }
    }

    private static string TypeDefinitionName(
        MetadataReader reader,
        TypeDefinitionHandle handle)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        return JoinTypeName(reader.GetString(type.Namespace), reader.GetString(type.Name));
    }

    private static string TypeReferenceName(
        MetadataReader reader,
        TypeReferenceHandle handle)
    {
        TypeReference type = reader.GetTypeReference(handle);
        return JoinTypeName(reader.GetString(type.Namespace), reader.GetString(type.Name));
    }

    private static string MethodDefinitionName(
        MetadataReader reader,
        MethodDefinitionHandle handle)
    {
        MethodDefinition method = reader.GetMethodDefinition(handle);
        string type = TypeDefinitionName(reader, method.GetDeclaringType());
        return $"{type}.{reader.GetString(method.Name)}";
    }

    private static string FieldDefinitionName(
        MetadataReader reader,
        FieldDefinitionHandle handle)
    {
        FieldDefinition field = reader.GetFieldDefinition(handle);
        string type = TypeDefinitionName(reader, field.GetDeclaringType());
        return $"{type}.{reader.GetString(field.Name)}";
    }

    private static string MemberReferenceName(
        MetadataReader reader,
        MemberReferenceHandle handle)
    {
        MemberReference member = reader.GetMemberReference(handle);
        string? parent = member.Parent.Kind switch
        {
            HandleKind.TypeDefinition => TypeDefinitionName(
                reader,
                (TypeDefinitionHandle)member.Parent),
            HandleKind.TypeReference => TypeReferenceName(
                reader,
                (TypeReferenceHandle)member.Parent),
            _ => null
        };
        string name = reader.GetString(member.Name);
        return parent is null ? name : $"{parent}.{name}";
    }

    private static string? MethodSpecificationName(
        MetadataReader reader,
        MethodSpecificationHandle handle)
    {
        MethodSpecification method = reader.GetMethodSpecification(handle);
        return method.Method.Kind switch
        {
            HandleKind.MethodDefinition => MethodDefinitionName(
                reader,
                (MethodDefinitionHandle)method.Method),
            HandleKind.MemberReference => MemberReferenceName(
                reader,
                (MemberReferenceHandle)method.Method),
            _ => null
        };
    }

    private static string UserString(MetadataReader reader, UserStringHandle handle)
    {
        const int maximumLength = 256;
        string value = reader.GetUserString(handle);
        string bounded = value.Length <= maximumLength
            ? value
            : string.Concat(value.AsSpan(0, maximumLength), "…");
        return $"\"{bounded.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static string JoinTypeName(string @namespace, string name) =>
        string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
}
