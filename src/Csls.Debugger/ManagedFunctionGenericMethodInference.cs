using Csls.Debugger.Contracts;
using System.Reflection;
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
        DebugExpressionLanguage language,
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

        List<ManagedBoundType>[] exactBounds =
            [.. Enumerable.Range(0, arity).Select(static _ => new List<ManagedBoundType>())];
        List<ManagedBoundType>[] lowerBounds =
            [.. Enumerable.Range(0, arity).Select(static _ => new List<ManagedBoundType>())];
        List<ManagedBoundType>[] upperBounds =
            [.. Enumerable.Range(0, arity).Select(static _ => new List<ManagedBoundType>())];
        for (int index = 0; index < signature.ParameterTypes.Length; index++)
        {
            int sourceIndex = parameterSourceIndices[index];
            if (sourceIndex < 0 || arguments[sourceIndex] is not ManagedBoundType argument)
            {
                continue;
            }

            if (!TryInferParameter(
                    signature.ParameterTypes[index], argument, exactBounds, lowerBounds, upperBounds,
                    GenericParameterAttributes.Covariant, types))
            {
                return null;
            }
        }

        var result = new ManagedBoundType[arity];
        for (int index = 0; index < arity; index++)
        {
            if (!TryFix(
                    exactBounds[index], lowerBounds[index], upperBounds[index], language,
                    types, thread, out result[index]))
            {
                return null;
            }
        }

        return ManagedFunctionGenericConstraintValidator.AreSatisfied(
            metadata, methodHandle, module, declaringArguments, result, types, thread)
            ? result : null;
    }

    private static bool TryInferParameter(
        ManagedMetadataTypeSignature signature,
        ManagedBoundType argument,
        IReadOnlyList<List<ManagedBoundType>> exactBounds,
        IReadOnlyList<List<ManagedBoundType>> lowerBounds,
        IReadOnlyList<List<ManagedBoundType>> upperBounds,
        GenericParameterAttributes inference,
        ManagedBoundTypeSystem types,
        int depth = 0)
    {
        if (depth >= 128)
        {
            return false;
        }

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
            }, argument.TypeArguments[0], exactBounds, lowerBounds, upperBounds,
                inference, types, depth + 1);
        }

        if (signature.GenericMethodParameterIndex is int index)
        {
            if ((uint)index >= (uint)exactBounds.Count)
            {
                return false;
            }

            List<ManagedBoundType> bounds = inference switch
            {
                GenericParameterAttributes.Covariant => lowerBounds[index],
                GenericParameterAttributes.Contravariant => upperBounds[index],
                _ => exactBounds[index]
            };
            AddDistinct(bounds, argument);
            return true;
        }

        if (signature.TypeArguments.Count == 0 ||
            !string.Equals(signature.MetadataName, argument.Name, StringComparison.Ordinal) ||
            signature.TypeArguments.Count != argument.TypeArguments.Count)
        {
            return true;
        }

        IReadOnlyList<GenericParameterAttributes> variance = types.GetVariance(argument);
        if (variance.Count != signature.TypeArguments.Count)
        {
            return false;
        }

        for (int childIndex = 0; childIndex < signature.TypeArguments.Count; childIndex++)
        {
            if (!TryInferParameter(
                signature.TypeArguments[childIndex], argument.TypeArguments[childIndex],
                exactBounds, lowerBounds, upperBounds,
                ComposeInference(inference, variance[childIndex]), types, depth + 1))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryFix(
        List<ManagedBoundType> exactBounds,
        List<ManagedBoundType> lowerBounds,
        List<ManagedBoundType> upperBounds,
        DebugExpressionLanguage language,
        ManagedBoundTypeSystem types,
        nint thread,
        out ManagedBoundType inferred)
    {
        inferred = null!;
        var references = new ManagedReferenceConversion(types);
        if (exactBounds.Count != 0)
        {
            ManagedBoundType exact = exactBounds[0];
            if (!exactBounds.All(exact.IsSameType) ||
                !lowerBounds.All(bound => HasImplicitConversion(bound, exact, language, references, thread)) ||
                !upperBounds.All(bound => HasImplicitConversion(exact, bound, language, references, thread)))
            {
                return false;
            }

            inferred = exact;
            return true;
        }

        List<ManagedBoundType> candidates = [];
        foreach (ManagedBoundType bound in lowerBounds.Concat(upperBounds))
        {
            AddDistinct(candidates, bound);
        }

        ManagedBoundType[] compatible = [.. candidates.Where(candidate =>
            lowerBounds.All(bound => HasImplicitConversion(bound, candidate, language, references, thread)) &&
            upperBounds.All(bound => HasImplicitConversion(candidate, bound, language, references, thread)))];
        ManagedBoundType[] best = [.. compatible.Where(candidate => compatible.All(
            other => HasImplicitConversion(other, candidate, language, references, thread)))];
        if (best.Length != 1)
        {
            return false;
        }

        inferred = best[0];
        return true;
    }

    private static GenericParameterAttributes ComposeInference(
        GenericParameterAttributes outer,
        GenericParameterAttributes inner)
    {
        outer &= GenericParameterAttributes.VarianceMask;
        inner &= GenericParameterAttributes.VarianceMask;
        if (outer == GenericParameterAttributes.None || inner == GenericParameterAttributes.None)
        {
            return GenericParameterAttributes.None;
        }

        return outer == inner
            ? GenericParameterAttributes.Covariant
            : GenericParameterAttributes.Contravariant;
    }

    private static bool HasImplicitConversion(
        ManagedBoundType source,
        ManagedBoundType destination,
        DebugExpressionLanguage language,
        ManagedReferenceConversion references,
        nint thread) => source.IsSameType(destination) ||
        references.IsImplicit(source, destination, thread) ||
        ManagedPrimitiveConversionEvaluator.IsImplicitInvocationConversion(source, destination, language);

    private static void AddDistinct(List<ManagedBoundType> bounds, ManagedBoundType argument)
    {
        if (!bounds.Any(argument.IsSameType))
        {
            bounds.Add(argument);
        }
    }
}
