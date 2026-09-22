using System.Reflection;
using System.Reflection.Metadata;

namespace Csls.Debugger;

/// <summary>
/// Validates inferred method arguments against their loaded CLR constraints.
/// </summary>
internal static class ManagedFunctionGenericConstraintValidator
{
    private const int MaximumConstraintsPerParameter = 128;

    /// <summary>
    /// Checks special and declared type constraints before any target code executes.
    /// </summary>
    internal static bool AreSatisfied(
        ManagedMetadataImage metadata,
        MethodDefinitionHandle methodHandle,
        nint module,
        IReadOnlyList<ManagedBoundType> declaringArguments,
        IReadOnlyList<ManagedBoundType> methodArguments,
        ManagedBoundTypeSystem types,
        nint thread)
    {
        (MetadataReader reader, EntityHandle local) = metadata.Resolve(methodHandle);
        GenericParameterHandleCollection parameters = reader.GetMethodDefinition((MethodDefinitionHandle)local)
            .GetGenericParameters();
        if (parameters.Count != methodArguments.Count)
        {
            return false;
        }

        var conversions = new ManagedReferenceConversion(types);
        var provider = new ManagedMetadataTypeSignatureProvider(module, metadata);
        for (int index = 0; index < parameters.Count; index++)
        {
            GenericParameterHandle handle = parameters[index];
            GenericParameter parameter = reader.GetGenericParameter(handle);
            if (parameter.Index != index)
            {
                return false;
            }

            ManagedBoundType argument = methodArguments[index];
            GenericParameterAttributes attributes = parameter.Attributes;
            if (!SatisfiesSpecialConstraints(argument, attributes, types, thread) ||
                HasUnmanagedConstraint(reader, parameter))
            {
                return false;
            }

            GenericParameterConstraintHandleCollection constraints = parameter.GetConstraints();
            if (constraints.Count > MaximumConstraintsPerParameter)
            {
                return false;
            }

            foreach (GenericParameterConstraintHandle constraintHandle in constraints)
            {
                EntityHandle constraint = reader.GetGenericParameterConstraint(constraintHandle).Type;
                ManagedMetadataTypeSignature signature = DecodeConstraint(provider, metadata.Baseline, constraint);
                ManagedBoundType required = types.Bind(signature, declaringArguments, methodArguments, thread);
                if (!conversions.IsRuntimeAssignable(argument, required, thread))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool SatisfiesSpecialConstraints(
        ManagedBoundType argument,
        GenericParameterAttributes attributes,
        ManagedBoundTypeSystem types,
        nint thread)
    {
        if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0 && !argument.IsReference)
        {
            return false;
        }

        if ((attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0 &&
            (argument.IsReference || types.IsCoreType(argument, "System.Nullable`1", thread)))
        {
            return false;
        }

        return (attributes & GenericParameterAttributes.DefaultConstructorConstraint) == 0 ||
            types.HasPublicParameterlessConstructor(argument);
    }

    private static bool HasUnmanagedConstraint(MetadataReader reader, GenericParameter parameter) =>
        parameter.GetCustomAttributes().Any(handle =>
            ManagedDebuggerAttributeReader.GetAttributeTypeName(reader, reader.GetCustomAttribute(handle)) ==
            "System.Runtime.CompilerServices.IsUnmanagedAttribute");

    private static ManagedMetadataTypeSignature DecodeConstraint(
        ManagedMetadataTypeSignatureProvider provider,
        MetadataReader reader,
        EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => provider.GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0x12),
            HandleKind.TypeReference => provider.GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0x12),
            HandleKind.TypeSpecification => provider.GetTypeFromSpecification(
                reader, null, (TypeSpecificationHandle)handle, 0x12),
            _ => throw new BadImageFormatException("A generic constraint is not a type token.")
        };
}
