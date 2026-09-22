using System.Reflection.Metadata;

namespace Csls.Debugger;

/// <summary>
/// Infers exact loaded method type arguments from supplied call arguments.
/// </summary>
internal static class ManagedFunctionGenericMethodInference
{
    /// <summary>
    /// Returns inferred arguments that satisfy the loaded declaration, or null when inference is incomplete.
    /// </summary>
    internal static ManagedBoundType[]? TryInfer(
        ManagedMetadataImage metadata,
        MethodDefinitionHandle methodHandle,
        MethodSignature<ManagedMetadataTypeSignature> signature,
        IReadOnlyList<int> parameterSourceIndices,
        IReadOnlyList<ManagedBoundType?> arguments,
        IReadOnlyList<ManagedBoundType> declaringArguments,
        ManagedBoundTypeSystem types,
        nint module,
        nint thread)
    {
        if (!signature.Header.IsGeneric)
        {
            return [];
        }

        int arity = signature.GenericParameterCount;
        if (arity is < 1 or > 64)
        {
            return null;
        }

        (MetadataReader reader, EntityHandle local) = metadata.Resolve(methodHandle);
        GenericParameterHandleCollection parameters = reader.GetMethodDefinition((MethodDefinitionHandle)local)
            .GetGenericParameters();
        if (parameters.Count != arity)
        {
            return null;
        }

        var inferred = new ManagedBoundType?[arity];
        for (int index = 0; index < signature.ParameterTypes.Length; index++)
        {
            int sourceIndex = parameterSourceIndices[index];
            if (sourceIndex < 0 || arguments[sourceIndex] is not ManagedBoundType argument)
            {
                continue;
            }

            if (!TryInferParameter(signature.ParameterTypes[index], argument, inferred))
            {
                return null;
            }
        }

        if (inferred.Any(static argument => argument is null))
        {
            return null;
        }

        ManagedBoundType[] result = [.. inferred.Select(static argument => argument!)];
        return ManagedFunctionGenericConstraintValidator.AreSatisfied(
            metadata, methodHandle, module, declaringArguments, result, types, thread)
            ? result : null;
    }

    private static bool TryInferParameter(
        ManagedMetadataTypeSignature signature,
        ManagedBoundType argument,
        ManagedBoundType?[] inferred)
    {
        if (signature.ArrayShapes.Count != 0)
        {
            ManagedMetadataArrayShape shape = signature.ArrayShapes[^1];
            if (!argument.IsArray || argument.ArrayRank != shape.Rank ||
                (argument.ElementType == 0x1d) != shape.IsVector ||
                argument.TypeArguments.Count != 1)
            {
                return true;
            }

            return TryInferParameter(signature with
            {
                ArrayShapes = [.. signature.ArrayShapes.Take(signature.ArrayShapes.Count - 1)]
            }, argument.TypeArguments[0], inferred);
        }

        if (signature.GenericMethodParameterIndex is int index)
        {
            if ((uint)index >= (uint)inferred.Length)
            {
                return false;
            }

            if (inferred[index] is ManagedBoundType previous)
            {
                return previous.IsSameType(argument);
            }

            inferred[index] = argument;
            return true;
        }

        if (signature.TypeArguments.Count == 0 ||
            !string.Equals(signature.MetadataName, argument.Name, StringComparison.Ordinal) ||
            signature.TypeArguments.Count != argument.TypeArguments.Count)
        {
            return true;
        }

        for (int childIndex = 0; childIndex < signature.TypeArguments.Count; childIndex++)
        {
            if (!TryInferParameter(
                signature.TypeArguments[childIndex], argument.TypeArguments[childIndex], inferred))
            {
                return false;
            }
        }

        return true;
    }
}
