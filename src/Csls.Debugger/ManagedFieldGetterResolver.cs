using Csls.Debugger.Contracts;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Csls.Debugger;

/// <summary>
/// Proves that an instance property returns one existing field using its current runtime IL.
/// </summary>
internal static class ManagedFieldGetterResolver
{
    /// <summary>
    /// Resolves a field-backed property declared at one exact runtime hierarchy level.
    /// </summary>
    /// <param name="module">The borrowed runtime module containing the property.</param>
    /// <param name="metadata">The module metadata defining the property and its signature.</param>
    /// <param name="typeToken">The property's exact declaring type token.</param>
    /// <param name="name">The source-language property name.</param>
    /// <param name="language">The source-language identifier comparison policy.</param>
    /// <param name="metadataDeltas">The accepted metadata generations of this loaded module.</param>
    /// <returns>The proven field and property tuple names, or null when no matching property is declared.</returns>
    internal static (uint FieldToken, ManagedTupleCustomTypeInfo? TupleCustomTypeInfo)? Resolve(
        nint module, MetadataReader metadata, uint typeToken, string name,
        DebugExpressionLanguage language, IReadOnlyList<byte[]> metadataDeltas)
    {
        var typeHandle = (TypeDefinitionHandle)MetadataTokens.EntityHandle(checked((int)typeToken));
        StringComparison comparison = language == DebugExpressionLanguage.VisualBasic
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        PropertyDefinitionHandle[] matches = [.. metadata.GetTypeDefinition(typeHandle).GetProperties()
            .Where(handle => string.Equals(metadata.GetString(metadata.GetPropertyDefinition(handle).Name), name, comparison))];
        if (matches.Length == 0)
        {
            return null;
        }
        if (matches.Length != 1)
        {
            throw new InvalidOperationException($"Property '{name}' is ambiguous on the selected runtime type.");
        }

        MethodDefinitionHandle getter = metadata.GetPropertyDefinition(matches[0]).GetAccessors().Getter;
        if (!getter.IsNil)
        {
            using var currentMetadata = new ManagedMetadataImage(metadata, metadataDeltas);
            MethodDefinition method = currentMetadata.GetMethodDefinition(getter);
            if ((method.Attributes & (MethodAttributes.Static | MethodAttributes.Abstract | MethodAttributes.Virtual)) == 0 &&
                (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                    MethodImplAttributes.Synchronized)) == MethodImplAttributes.IL &&
                CorDebugMethodBodyReader.Read(module, checked((uint)MetadataTokens.GetToken(getter)),
                    ManagedFieldGetterDecoder.MaximumMethodBodyBytes, out uint locals) is byte[] body &&
                ManagedFieldGetterDecoder.TryDecode(body, out int fieldToken, out int returnLocal) &&
                MetadataTokens.EntityHandle(fieldToken) is { Kind: HandleKind.FieldDefinition } entity)
            {
                FieldDefinition field = metadata.GetFieldDefinition((FieldDefinitionHandle)entity);
                if (field.GetDeclaringType() == typeHandle && (field.Attributes & FieldAttributes.Static) == 0 &&
                    ManagedFieldGetterSignature.Matches(currentMetadata, method, field, locals, returnLocal))
                {
                    return (checked((uint)fieldToken), ManagedTupleElementNameReader.ReadAttribute(
                        currentMetadata, matches[0]));
                }
            }
        }

        throw new InvalidOperationException($"Property '{name}' requires target-code evaluation.");
    }
}
