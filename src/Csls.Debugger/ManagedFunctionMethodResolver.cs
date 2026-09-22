using Csls.Debugger.Contracts;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Selects concrete callable declarations using exact loaded argument identities.
/// </summary>
internal static class ManagedFunctionMethodResolver
{
    /// <summary>
    /// Opens the current module metadata while selecting one callable declaration.
    /// </summary>
    internal static uint? Resolve(
        CorDebugLoadedModule module,
        uint typeToken,
        string methodName,
        DebugExpressionLanguage language,
        IReadOnlyList<ManagedBoundType?> arguments,
        bool staticMethod,
        ManagedBoundTypeSystem types,
        nint thread,
        IReadOnlyList<ManagedBoundType>? declaringTypeArguments = null)
    {
        return ResolveCall(module, typeToken, methodName, language, arguments,
            staticMethod, types, thread, declaringTypeArguments)?.Token;
    }

    /// <summary>
    /// Resolves a callable declaration together with its exact bound parameter types.
    /// </summary>
    internal static (uint Token, ManagedBoundType[] Parameters, int[] ParameterSourceIndices,
        ManagedExpressionValue?[] OptionalArguments, ManagedBoundType[] MethodTypeArguments)? ResolveCall(
        CorDebugLoadedModule module,
        uint typeToken,
        string methodName,
        DebugExpressionLanguage language,
        IReadOnlyList<ManagedBoundType?> arguments,
        bool staticMethod,
        ManagedBoundTypeSystem types,
        nint thread,
        IReadOnlyList<ManagedBoundType>? declaringTypeArguments = null,
        IReadOnlyList<ManagedExpressionValue?>? constantArguments = null,
        IReadOnlyList<string?>? argumentNames = null)
    {
        using PEReader? reader = module.OpenPeReader();
        if (reader is null)
        {
            throw new InvalidOperationException("The method's runtime metadata is unavailable.");
        }

        using var metadata = new ManagedMetadataImage(reader.GetMetadataReader(), module.MetadataDeltas);
        return ResolveCall(metadata, module.Pointer, typeToken, methodName, language, arguments,
            staticMethod, types, thread, declaringTypeArguments, constantArguments, argumentNames);
    }

    /// <summary>
    /// Resolves the unique best applicable declaration before target execution.
    /// </summary>
    internal static uint? Resolve(
        ManagedMetadataImage metadata,
        nint module,
        uint typeToken,
        string methodName,
        DebugExpressionLanguage language,
        IReadOnlyList<ManagedBoundType?> arguments,
        bool staticMethod,
        ManagedBoundTypeSystem types,
        nint thread,
        IReadOnlyList<ManagedBoundType>? declaringTypeArguments = null)
    {
        return ResolveCall(metadata, module, typeToken, methodName, language, arguments,
            staticMethod, types, thread, declaringTypeArguments)?.Token;
    }

    /// <summary>
    /// Resolves the unique best declaration and preserves its bound parameter types.
    /// </summary>
    internal static (uint Token, ManagedBoundType[] Parameters, int[] ParameterSourceIndices,
        ManagedExpressionValue?[] OptionalArguments, ManagedBoundType[] MethodTypeArguments)? ResolveCall(
        ManagedMetadataImage metadata,
        nint module,
        uint typeToken,
        string methodName,
        DebugExpressionLanguage language,
        IReadOnlyList<ManagedBoundType?> arguments,
        bool staticMethod,
        ManagedBoundTypeSystem types,
        nint thread,
        IReadOnlyList<ManagedBoundType>? declaringTypeArguments = null,
        IReadOnlyList<ManagedExpressionValue?>? constantArguments = null,
        IReadOnlyList<string?>? argumentNames = null)
    {
        EntityHandle entity = MetadataTokens.EntityHandle(checked((int)typeToken));
        if (entity.Kind != HandleKind.TypeDefinition)
        {
            throw new BadImageFormatException(
                $"Runtime type token 0x{typeToken:X8} is not a TypeDef token.");
        }

        var typeHandle = (TypeDefinitionHandle)entity;
        TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
        StringComparison comparison = language == DebugExpressionLanguage.VisualBasic
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var conversions = new ManagedReferenceConversion(types);
        var matches = new List<(MethodDefinitionHandle Handle, ManagedBoundType[] Parameters,
            int[] ParameterSourceIndices, ManagedExpressionValue?[] OptionalArguments,
            ManagedBoundType[] MethodTypeArguments)>();
        foreach (MethodDefinitionHandle methodHandle in metadata.GetMethods(typeHandle))
        {
            MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
            bool methodIsStatic = (method.Attributes & MethodAttributes.Static) != 0;
            if ((method.Attributes & MethodAttributes.Abstract) != 0 ||
                methodIsStatic != staticMethod ||
                !string.Equals(metadata.GetString(method.Name), methodName, comparison))
            {
                continue;
            }

            MethodSignature<ManagedMetadataTypeSignature> signature =
                metadata.DecodeMethodSignature(methodHandle, module);
            if (signature.ParameterTypes.Length < arguments.Count ||
                signature.ParameterTypes.Length > 64 ||
                signature.ParameterTypes.Any(static parameter => parameter.UnsupportedKind is not null))
            {
                continue;
            }

            int[]? parameterSourceIndices = ManagedFunctionArgumentMap.TryCreate(
                metadata, methodHandle, argumentNames, arguments.Count,
                signature.ParameterTypes.Length, language);
            if (parameterSourceIndices is null)
            {
                continue;
            }

            ManagedBoundType[]? methodTypeArguments = ManagedFunctionGenericMethodInference.TryInfer(
                metadata, methodHandle, signature, parameterSourceIndices, arguments,
                declaringTypeArguments ?? [], types, module, thread);
            if (methodTypeArguments is null)
            {
                continue;
            }

            ManagedBoundType[] declaredParameters = [.. signature.ParameterTypes.Select(parameter =>
                types.Bind(parameter, declaringTypeArguments ?? [], methodTypeArguments, thread))];
            ManagedExpressionValue?[]? optionalArguments = ManagedFunctionOptionalArguments.TryCreate(
                metadata, methodHandle, declaredParameters, parameterSourceIndices, types, module, thread);
            if (optionalArguments is null)
            {
                continue;
            }

            var parameters = new ManagedBoundType[arguments.Count];
            for (int index = 0; index < declaredParameters.Length; index++)
            {
                if (parameterSourceIndices[index] >= 0)
                {
                    parameters[parameterSourceIndices[index]] = declaredParameters[index];
                }
            }

            if (IsApplicable(arguments, parameters, constantArguments, language, conversions, types, thread))
            {
                matches.Add((methodHandle, parameters, parameterSourceIndices,
                    optionalArguments, methodTypeArguments));
            }
        }

        if (matches.Count == 0)
        {
            return null;
        }

        (MethodDefinitionHandle Handle, ManagedBoundType[] Parameters, int[] ParameterSourceIndices,
            ManagedExpressionValue?[] OptionalArguments, ManagedBoundType[] MethodTypeArguments)[] bestMatches =
            [.. matches.Where(candidate => !matches.Any(other =>
                other.Handle != candidate.Handle &&
                (IsBetter(other.Parameters, candidate.Parameters, arguments, language, conversions, thread) ||
                 HasEqualParameterPreference(other.Parameters, candidate.Parameters,
                     other.OptionalArguments, candidate.OptionalArguments,
                     other.MethodTypeArguments, candidate.MethodTypeArguments))))];
        if (bestMatches.Length != 1)
        {
            string typeName = metadata.GetString(type.Name);
            throw new InvalidOperationException(
                $"Method call '{methodName}' with {arguments.Count} argument(s) is " +
                $"ambiguous on runtime type '{typeName}'.");
        }

        return (checked((uint)MetadataTokens.GetToken(bestMatches[0].Handle)),
            bestMatches[0].Parameters, bestMatches[0].ParameterSourceIndices,
            bestMatches[0].OptionalArguments, bestMatches[0].MethodTypeArguments);
    }

    private static bool IsApplicable(
        IReadOnlyList<ManagedBoundType?> arguments,
        ManagedBoundType[] parameters,
        IReadOnlyList<ManagedExpressionValue?>? constantArguments,
        DebugExpressionLanguage language,
        ManagedReferenceConversion conversions,
        ManagedBoundTypeSystem types,
        nint thread)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            ManagedBoundType? argument = arguments[index];
            ManagedBoundType parameter = parameters[index];
            if (constantArguments?[index]?.IsContextualDefault == true)
            {
                if (ManagedFunctionImplicitDefaults.TryCreateContextual(parameter, types, thread) is null)
                {
                    return false;
                }

                continue;
            }

            if (argument is null)
            {
                if (!parameter.IsReference)
                {
                    return false;
                }
            }
            else if (!argument.IsSameType(parameter) &&
                !conversions.IsImplicit(argument, parameter, thread) &&
                !ManagedPrimitiveConversionEvaluator.IsImplicitInvocationConversion(argument, parameter, language) &&
                !(constantArguments?[index] is ManagedExpressionValue constant &&
                    ManagedPrimitiveConversionEvaluator.IsImplicitConstantInvocationConversion(
                        constant, argument, parameter, language)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasEqualParameterPreference(
        ManagedBoundType[] candidateParameters,
        ManagedBoundType[] otherParameters,
        ManagedExpressionValue?[] candidateOptionalArguments,
        ManagedExpressionValue?[] otherOptionalArguments,
        ManagedBoundType[] candidateMethodArguments,
        ManagedBoundType[] otherMethodArguments)
    {
        if (!candidateParameters.Zip(otherParameters).All(static pair =>
                pair.First.IsSameType(pair.Second)))
        {
            return false;
        }

        bool candidateUsesOptional = candidateOptionalArguments.Any(static value => value is not null);
        bool otherUsesOptional = otherOptionalArguments.Any(static value => value is not null);
        if (candidateUsesOptional != otherUsesOptional)
        {
            return !candidateUsesOptional;
        }

        return candidateMethodArguments.Length == 0 && otherMethodArguments.Length != 0;
    }

    private static bool IsBetter(
        ManagedBoundType[] candidate,
        ManagedBoundType[] other,
        IReadOnlyList<ManagedBoundType?> arguments,
        DebugExpressionLanguage language,
        ManagedReferenceConversion conversions,
        nint thread)
    {
        bool strictlyBetter = false;
        for (int index = 0; index < candidate.Length; index++)
        {
            ManagedBoundType preferred = candidate[index];
            ManagedBoundType alternative = other[index];
            if (preferred.IsSameType(alternative))
            {
                continue;
            }

            ManagedBoundType? argument = arguments[index];
            if (argument?.IsSameType(preferred) == true)
            {
                strictlyBetter = true;
                continue;
            }

            bool preferredToAlternative = conversions.IsImplicit(preferred, alternative, thread) ||
                ManagedPrimitiveConversionEvaluator.IsImplicitInvocationConversion(preferred, alternative, language);
            bool alternativeToPreferred = conversions.IsImplicit(alternative, preferred, thread) ||
                ManagedPrimitiveConversionEvaluator.IsImplicitInvocationConversion(alternative, preferred, language);
            bool preferredSignedTarget = ManagedPrimitiveConversionEvaluator.IsPreferredSignedInvocationTarget(
                preferred, alternative, language);
            if (argument?.IsSameType(alternative) == true ||
                !(preferredToAlternative && !alternativeToPreferred || preferredSignedTarget))
            {
                return false;
            }

            strictlyBetter = true;
        }

        return strictlyBetter;
    }
}
