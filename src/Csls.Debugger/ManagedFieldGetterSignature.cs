using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Csls.Debugger;

/// <summary>
/// Verifies that a recognized getter preserves exact field storage through its return and optional local signature.
/// </summary>
internal static class ManagedFieldGetterSignature
{
    private const int MaximumSignatureBytes = 4096;
    private const int MaximumLocalCount = 256;

    /// <summary>
    /// Matches complete encoded type identities so a return local cannot narrow or reinterpret the field value.
    /// </summary>
    internal static bool Matches(ManagedMetadataImage metadata, MethodDefinition method, FieldDefinition field,
        uint localSignatureToken, int returnLocal)
    {
        BlobReader fieldSignature = metadata.GetBlobReader(field.Signature);
        BlobReader methodSignature = metadata.GetBlobReader(method.Signature);
        if (fieldSignature.Length > MaximumSignatureBytes || methodSignature.Length > MaximumSignatureBytes ||
            fieldSignature.ReadSignatureHeader().Kind != SignatureKind.Field)
        {
            return false;
        }
        SignatureHeader header = methodSignature.ReadSignatureHeader();
        if (header.Kind != SignatureKind.Method || !header.IsInstance || header.IsGeneric ||
            header.HasExplicitThis || header.CallingConvention != SignatureCallingConvention.Default ||
            methodSignature.ReadCompressedInteger() != 0)
        {
            return false;
        }
        byte[] fieldType = fieldSignature.ReadBytes(fieldSignature.RemainingBytes);
        if (fieldType.Length == 0 || !fieldType.AsSpan().SequenceEqual(methodSignature.ReadBytes(methodSignature.RemainingBytes)))
        {
            return false;
        }
        if (returnLocal < 0)
        {
            return true;
        }

        EntityHandle handle = MetadataTokens.EntityHandle(checked((int)localSignatureToken));
        if (handle.IsNil || handle.Kind != HandleKind.StandaloneSignature)
        {
            return false;
        }
        (MetadataReader owner, EntityHandle relative) = metadata.Resolve(handle);
        BlobReader locals = metadata.GetBlobReader(owner.GetStandaloneSignature((StandaloneSignatureHandle)relative).Signature);
        if (locals.Length > MaximumSignatureBytes || locals.ReadSignatureHeader().Kind != SignatureKind.LocalVariables)
        {
            return false;
        }
        int count = locals.ReadCompressedInteger();
        if (count > MaximumLocalCount || returnLocal >= count)
        {
            return false;
        }

        var decoder = new SignatureDecoder<string, object?>(new FunctionEvaluationSignatureTypeProvider(metadata),
            metadata.Baseline, genericContext: null);
        for (int index = 0; index < returnLocal; index++)
        {
            _ = decoder.DecodeType(ref locals);
        }
        int start = locals.Offset;
        _ = decoder.DecodeType(ref locals);
        int length = locals.Offset - start;
        locals.Offset = start;
        return fieldType.AsSpan().SequenceEqual(locals.ReadBytes(length));
    }
}
