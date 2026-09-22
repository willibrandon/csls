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
    internal static (uint Token, ManagedBoundType[] Parameters)? ResolveCall(
        CorDebugLoadedModule module,
        uint typeToken,
        string methodName,
        DebugExpressionLanguage language,
        IReadOnlyList<ManagedBoundType?> arguments,
        bool staticMethod,
        ManagedBoundTypeSystem types,
        nint thread,
        IReadOnlyList<ManagedBoundType>? declaringTypeArguments = null,
        IReadOnlyList<ManagedExpressionValue?>? constantArguments = null)
    {
        using PEReader? reader = module.OpenPeReader();
        if (reader is null)
        {
            throw new InvalidOperationException("The method's runtime metadata is unavailable.");
        }

        using var metadata = new ManagedMetadataImage(reader.GetMetadataReader(), module.MetadataDeltas);
        return ResolveCall(metadata, module.Pointer, typeToken, methodName, language, arguments,
            staticMethod, types, thread, declaringTypeArguments, constantArguments);
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
    internal static (uint Token, ManagedBoundType[] Parameters)? ResolveCall(
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
        IReadOnlyList<ManagedExpressionValue?>? constantArguments = null)
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
        var matches = new List<(MethodDefinitionHandle Handle, ManagedBoundType[] Parameters)>();
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
            if (signature.Header.IsGeneric || signature.ParameterTypes.Length != arguments.Count ||
                signature.ParameterTypes.Any(static parameter => parameter.UnsupportedKind is not null))
            {
                continue;
            }

            ManagedBoundType[] parameters = [.. signature.ParameterTypes.Select(parameter =>
                types.Bind(parameter, declaringTypeArguments ?? [], [], thread))];
            if (IsApplicable(arguments, parameters, constantArguments, language, conversions, thread))
            {
                matches.Add((methodHandle, parameters));
            }
        }

        if (matches.Count == 0)
        {
            return null;
        }

        (MethodDefinitionHandle Handle, ManagedBoundType[] Parameters)[] bestMatches =
            [.. matches.Where(candidate => !matches.Any(other =>
                other.Handle != candidate.Handle &&
                IsBetter(other.Parameters, candidate.Parameters, arguments, language, conversions, thread)))];
        if (bestMatches.Length != 1)
        {
            string typeName = metadata.GetString(type.Name);
            throw new InvalidOperationException(
                $"Method call '{methodName}' with {arguments.Count} argument(s) is " +
                $"ambiguous on runtime type '{typeName}'.");
        }

        return (checked((uint)MetadataTokens.GetToken(bestMatches[0].Handle)), bestMatches[0].Parameters);
    }

    private static bool IsApplicable(
        IReadOnlyList<ManagedBoundType?> arguments,
        ManagedBoundType[] parameters,
        IReadOnlyList<ManagedExpressionValue?>? constantArguments,
        DebugExpressionLanguage language,
        ManagedReferenceConversion conversions,
        nint thread)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            ManagedBoundType? argument = arguments[index];
            ManagedBoundType parameter = parameters[index];
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
