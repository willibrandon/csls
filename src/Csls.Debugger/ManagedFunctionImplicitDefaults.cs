using System.Reflection.Metadata;

namespace Csls.Debugger;

/// <summary>
/// Materializes compiler defaults for optional parameters without metadata constants.
/// </summary>
internal static class ManagedFunctionImplicitDefaults
{
    /// <summary>
    /// Materializes an explicit contextual default from its selected parameter type.
    /// </summary>
    internal static ManagedExpressionValue? TryCreateContextual(
        ManagedBoundType parameter,
        ManagedBoundTypeSystem types,
        nint thread) => parameter.IsReference
            ? ManagedExpressionValueFactory.FromScalar(value: null, "object") with
            {
                DeclaredType = parameter
            }
            : TryCreate(parameter, types, thread);

    /// <summary>
    /// Gets a supported default while preserving the loaded parameter identity.
    /// </summary>
    internal static ManagedExpressionValue? TryCreate(
        ManagedBoundType parameter,
        ManagedBoundTypeSystem types,
        nint thread)
    {
        if (types.IsCoreType(parameter, "System.Object", thread))
        {
            return null;
        }

        if (types.IsCoreType(parameter, "System.Decimal", thread))
        {
            return ManagedExpressionValueFactory.FromScalar(decimal.Zero, "decimal") with
            {
                DeclaredType = parameter
            };
        }

        if (types.IsCoreType(parameter, "System.DateTime", thread))
        {
            return ManagedExpressionValueFactory.FromScalar(DateTime.MinValue, "System.DateTime") with
            {
                DeclaredType = parameter
            };
        }

        if (parameter.IsReference)
        {
            return ManagedExpressionValueFactory.FromScalar(value: null, "object") with
            {
                DeclaredType = parameter
            };
        }

        ManagedBoundType storage = types.TryGetEnumUnderlyingType(parameter, thread) ?? parameter;
        if (storage.ElementType == 0x11 && !types.IsByRefLike(storage))
        {
            return ManagedExpressionValueFactory.FromZeroValueTypeDefault(parameter);
        }

        (object? Value, string? Name) zero = storage.ElementType switch
        {
            0x02 => (false, "bool"),
            0x03 => ('\0', "char"),
            0x04 => ((sbyte)0, "sbyte"),
            0x05 => ((byte)0, "byte"),
            0x06 => ((short)0, "short"),
            0x07 => ((ushort)0, "ushort"),
            0x08 => (0, "int"),
            0x09 => (0U, "uint"),
            0x0a => (0L, "long"),
            0x0b => (0UL, "ulong"),
            0x0c => (0F, "float"),
            0x0d => (0D, "double"),
            0x18 => (0L, "nint"),
            0x19 => (0UL, "nuint"),
            _ => (null, null)
        };
        return zero.Name is null ? null : ManagedExpressionValueFactory.FromScalar(zero.Value, zero.Name);
    }

    /// <summary>
    /// Identifies compiler-synthesized call-site values and parameter collections.
    /// </summary>
    internal static bool HasCallSiteOrCollectionAttribute(
        ManagedMetadataImage metadata,
        ParameterHandle handle,
        nint module)
    {
        var provider = new ManagedMetadataTypeSignatureProvider(module, metadata);
        return metadata.GetCustomAttributes(handle)
            .Select(metadata.GetAttributeType)
            .Select(type => type.Kind switch
            {
                HandleKind.TypeDefinition => provider.GetTypeFromDefinition(
                    metadata.Baseline, (TypeDefinitionHandle)type, 0x12).MetadataName,
                HandleKind.TypeReference => provider.GetTypeFromReference(
                    metadata.Baseline, (TypeReferenceHandle)type, 0x12).MetadataName,
                _ => null
            })
            .Any(name => name is "System.Runtime.CompilerServices.CallerLineNumberAttribute" or
                "System.Runtime.CompilerServices.CallerFilePathAttribute" or
                "System.Runtime.CompilerServices.CallerMemberNameAttribute" or
                "System.Runtime.CompilerServices.CallerArgumentExpressionAttribute" or
                "System.ParamArrayAttribute" or
                "System.Runtime.CompilerServices.ParamCollectionAttribute");
    }
}
