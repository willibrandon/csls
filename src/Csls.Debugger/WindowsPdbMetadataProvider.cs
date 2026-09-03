using Microsoft.DiaSymReader;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Csls.Debugger;

/// <summary>
/// Exposes stable managed PE metadata to the native Windows PDB reader.
/// </summary>
internal sealed class WindowsPdbMetadataProvider : ISymReaderMetadataProvider
{
    private readonly MetadataReader _metadata;

    /// <summary>
    /// Creates a provider over metadata owned by a live PE reader.
    /// </summary>
    /// <param name="metadata">The module metadata that remains valid for the provider lifetime.</param>
    internal WindowsPdbMetadataProvider(MetadataReader metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _metadata = metadata;
    }

    /// <summary>
    /// Gets a reader-owned standalone-signature blob for the requested metadata token.
    /// </summary>
    /// <param name="standaloneSignatureToken">The standalone-signature metadata token.</param>
    /// <param name="signature">Receives the borrowed signature pointer.</param>
    /// <param name="length">Receives the signature length in bytes.</param>
    /// <returns>True when the token identifies a valid standalone signature.</returns>
    public unsafe bool TryGetStandaloneSignature(
        int standaloneSignatureToken,
        out byte* signature,
        out int length)
    {
        signature = null;
        length = 0;
        Handle entity = MetadataTokens.Handle(standaloneSignatureToken);
        if (entity.Kind != HandleKind.StandaloneSignature)
        {
            return false;
        }

        StandaloneSignature definition = _metadata.GetStandaloneSignature(
            (StandaloneSignatureHandle)entity);
        BlobReader blob = _metadata.GetBlobReader(definition.Signature);
        signature = blob.CurrentPointer;
        length = blob.Length;
        return length > 0;
    }

    /// <summary>
    /// Gets the namespace, name, and attributes for a type-definition token.
    /// </summary>
    /// <param name="typeDefinitionToken">The type-definition metadata token.</param>
    /// <param name="namespaceName">Receives the metadata namespace.</param>
    /// <param name="typeName">Receives the metadata type name.</param>
    /// <param name="attributes">Receives the reflection type attributes.</param>
    /// <returns>True when the token identifies a valid type definition.</returns>
    public bool TryGetTypeDefinitionInfo(
        int typeDefinitionToken,
        out string namespaceName,
        out string typeName,
        out TypeAttributes attributes)
    {
        namespaceName = string.Empty;
        typeName = string.Empty;
        attributes = default;
        Handle entity = MetadataTokens.Handle(typeDefinitionToken);
        if (entity.Kind != HandleKind.TypeDefinition)
        {
            return false;
        }

        TypeDefinition definition = _metadata.GetTypeDefinition((TypeDefinitionHandle)entity);
        namespaceName = _metadata.GetString(definition.Namespace);
        typeName = _metadata.GetString(definition.Name);
        attributes = definition.Attributes;
        return true;
    }

    /// <summary>
    /// Gets the namespace and name for a type-reference token.
    /// </summary>
    /// <param name="typeReferenceToken">The type-reference metadata token.</param>
    /// <param name="namespaceName">Receives the metadata namespace.</param>
    /// <param name="typeName">Receives the metadata type name.</param>
    /// <returns>True when the token identifies a valid type reference.</returns>
    public bool TryGetTypeReferenceInfo(
        int typeReferenceToken,
        out string namespaceName,
        out string typeName)
    {
        namespaceName = string.Empty;
        typeName = string.Empty;
        Handle entity = MetadataTokens.Handle(typeReferenceToken);
        if (entity.Kind != HandleKind.TypeReference)
        {
            return false;
        }

        TypeReference reference = _metadata.GetTypeReference((TypeReferenceHandle)entity);
        namespaceName = _metadata.GetString(reference.Namespace);
        typeName = _metadata.GetString(reference.Name);
        return true;
    }
}
