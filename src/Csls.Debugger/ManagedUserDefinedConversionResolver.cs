using Csls.Debugger.Contracts;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves user-defined conversion operators from the current loaded metadata generation.
/// </summary>
internal sealed class ManagedUserDefinedConversionResolver
{
    private readonly ManagedBoundTypeSystem _types;
    private readonly nint _thread;
    private readonly DebugExpressionLanguage _language;
    private readonly ManagedReferenceConversion _referenceConversions;
    private readonly List<(ManagedBoundType Source, ManagedBoundType Destination,
        ManagedUserDefinedConversion? Result)> _cache = [];

    /// <summary>
    /// Creates a resolver scoped to one stopped managed thread and loaded type universe.
    /// </summary>
    /// <param name="types">The exact loaded type system.</param>
    /// <param name="thread">The borrowed managed thread used for core-library identity.</param>
    /// <param name="language">The source language controlling standard numeric conversions.</param>
    internal ManagedUserDefinedConversionResolver(
        ManagedBoundTypeSystem types,
        nint thread,
        DebugExpressionLanguage language)
    {
        ArgumentNullException.ThrowIfNull(types);
        _types = types;
        _thread = thread;
        _language = language;
        _referenceConversions = new ManagedReferenceConversion(types);
    }

    /// <summary>
    /// Resolves the unique exact implicit operator from a source type to a destination type.
    /// </summary>
    /// <param name="source">The exact loaded source type.</param>
    /// <param name="destination">The exact loaded destination type.</param>
    /// <returns>The unique conversion, or null when no exact unambiguous operator exists.</returns>
    internal ManagedUserDefinedConversion? Resolve(
        ManagedBoundType source,
        ManagedBoundType destination)
    {
        if (source.IsSameType(destination) || source.IsArray || destination.IsArray)
        {
            return null;
        }

        if ((_types.GetAttributes(source) & TypeAttributes.Interface) != 0 ||
            (_types.GetAttributes(destination) & TypeAttributes.Interface) != 0)
        {
            return null;
        }

        foreach ((ManagedBoundType cachedSource, ManagedBoundType cachedDestination,
            ManagedUserDefinedConversion? result) in _cache)
        {
            if (cachedSource.IsSameType(source) && cachedDestination.IsSameType(destination))
            {
                return result;
            }
        }

        ManagedBoundType participatingSource = StripNullable(source);
        ManagedBoundType participatingDestination = StripNullable(destination);
        var matches = new List<ManagedUserDefinedConversion>();
        foreach (ManagedBoundType declaringType in GetParticipatingTypes(
            participatingSource,
            participatingDestination,
            includeDestinationBaseTypes: false))
        {
            AddMatches(declaringType, source, destination, matches);
        }

        ManagedUserDefinedConversion? resolved = SelectBest(matches, source, destination);
        _cache.Add((source, destination, resolved));
        return resolved;
    }

    /// <summary>
    /// Resolves one explicit or implicit operator for an authored cast with standard input and reference-result conversions.
    /// </summary>
    /// <param name="source">The exact loaded cast source type.</param>
    /// <param name="destination">The exact loaded cast destination type.</param>
    /// <returns>The unique best conversion, or null when none exists.</returns>
    internal ManagedUserDefinedConversion? ResolveExplicit(
        ManagedBoundType source,
        ManagedBoundType destination)
    {
        if (source.IsSameType(destination) || source.IsArray || destination.IsArray ||
            _referenceConversions.IsExplicit(source, destination, _thread) ||
            HasSupportedStandardUnboxingConversion(source, destination) ||
            (_types.GetAttributes(source) & TypeAttributes.Interface) != 0 ||
            (_types.GetAttributes(destination) & TypeAttributes.Interface) != 0)
        {
            return null;
        }

        var matches = new List<ManagedUserDefinedConversion>();
        foreach (ManagedBoundType declaringType in GetParticipatingTypes(
            StripNullable(source),
            StripNullable(destination),
            includeDestinationBaseTypes: true))
        {
            AddExplicitMatches(declaringType, source, destination, matches);
        }

        return SelectBest(matches, source, destination);
    }

    private void AddExplicitMatches(
        ManagedBoundType declaringType,
        ManagedBoundType source,
        ManagedBoundType destination,
        List<ManagedUserDefinedConversion> matches)
    {
        CorDebugLoadedModule module = _types.GetModule(declaringType);
        using PEReader? reader = module.OpenPeReader();
        if (reader is null)
        {
            return;
        }

        using var metadata = new ManagedMetadataImage(reader.GetMetadataReader(), module.MetadataDeltas);
        EntityHandle entity = MetadataTokens.EntityHandle(checked((int)declaringType.DefinitionToken));
        if (entity.Kind != HandleKind.TypeDefinition)
        {
            throw new BadImageFormatException(
                $"Runtime type token 0x{declaringType.DefinitionToken:X8} is not a TypeDef token.");
        }

        foreach (MethodDefinitionHandle handle in metadata.GetMethods((TypeDefinitionHandle)entity))
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            const MethodAttributes required = MethodAttributes.Public |
                MethodAttributes.Static | MethodAttributes.SpecialName;
            string name = metadata.GetString(method.Name);
            if ((method.Attributes & required) != required ||
                (method.Attributes & MethodAttributes.Abstract) != 0 ||
                method.GetGenericParameters().Count != 0 ||
                name is not ("op_Explicit" or "op_Implicit"))
            {
                continue;
            }

            MethodSignature<ManagedMetadataTypeSignature> signature =
                metadata.DecodeMethodSignature(handle, module.Pointer);
            if (signature.ParameterTypes is not [ManagedMetadataTypeSignature parameter] ||
                parameter.UnsupportedKind is not null ||
                signature.ReturnType.UnsupportedKind is not null)
            {
                continue;
            }

            ManagedBoundType parameterType = _types.Bind(
                parameter, declaringType.TypeArguments, [], _thread);
            ManagedBoundType resultType = _types.Bind(
                signature.ReturnType, declaringType.TypeArguments, [], _thread);
            uint methodToken = checked((uint)MetadataTokens.GetToken(handle));
            bool lifted = IsApplicableLiftedConversion(
                source, destination, parameterType, resultType);
            bool normal = !lifted &&
                (HasSupportedStandardExplicitInputConversion(source, parameterType) ||
                    HasSupportedExplicitNullableInputConversion(source, parameterType)) &&
                (HasSupportedStandardExplicitResultConversion(resultType, destination) ||
                    HasSupportedExplicitNullableResultConversion(resultType, destination));
            if ((normal || lifted) &&
                !matches.Any(match =>
                    match.DeclaringType.ModuleId == declaringType.ModuleId &&
                    match.MethodToken == methodToken))
            {
                matches.Add(new ManagedUserDefinedConversion(
                    declaringType,
                    methodToken,
                    parameterType,
                    resultType,
                    destination,
                    _language,
                    lifted));
            }
        }
    }

    private void AddMatches(
        ManagedBoundType declaringType,
        ManagedBoundType source,
        ManagedBoundType destination,
        List<ManagedUserDefinedConversion> matches)
    {
        CorDebugLoadedModule module = _types.GetModule(declaringType);
        using PEReader? reader = module.OpenPeReader();
        if (reader is null)
        {
            return;
        }

        using var metadata = new ManagedMetadataImage(reader.GetMetadataReader(), module.MetadataDeltas);
        EntityHandle entity = MetadataTokens.EntityHandle(checked((int)declaringType.DefinitionToken));
        if (entity.Kind != HandleKind.TypeDefinition)
        {
            throw new BadImageFormatException(
                $"Runtime type token 0x{declaringType.DefinitionToken:X8} is not a TypeDef token.");
        }

        foreach (MethodDefinitionHandle handle in metadata.GetMethods((TypeDefinitionHandle)entity))
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            const MethodAttributes required = MethodAttributes.Public |
                MethodAttributes.Static | MethodAttributes.SpecialName;
            if ((method.Attributes & required) != required ||
                (method.Attributes & MethodAttributes.Abstract) != 0 ||
                method.GetGenericParameters().Count != 0 ||
                !string.Equals(metadata.GetString(method.Name), "op_Implicit", StringComparison.Ordinal))
            {
                continue;
            }

            MethodSignature<ManagedMetadataTypeSignature> signature =
                metadata.DecodeMethodSignature(handle, module.Pointer);
            if (signature.ParameterTypes is not [ManagedMetadataTypeSignature parameter] ||
                parameter.UnsupportedKind is not null ||
                signature.ReturnType.UnsupportedKind is not null)
            {
                continue;
            }

            ManagedBoundType parameterType = _types.Bind(
                parameter, declaringType.TypeArguments, [], _thread);
            ManagedBoundType resultType = _types.Bind(
                signature.ReturnType, declaringType.TypeArguments, [], _thread);
            bool normal = HasStandardImplicitConversion(source, parameterType) &&
                HasStandardImplicitConversion(resultType, destination);
            bool lifted = !normal && IsApplicableLiftedConversion(
                source, destination, parameterType, resultType);
            if ((normal || lifted) &&
                !matches.Any(match =>
                    match.DeclaringType.ModuleId == declaringType.ModuleId &&
                    match.MethodToken == checked((uint)MetadataTokens.GetToken(handle))))
            {
                matches.Add(new ManagedUserDefinedConversion(
                    declaringType,
                    checked((uint)MetadataTokens.GetToken(handle)),
                    parameterType,
                    resultType,
                    destination,
                    _language,
                    lifted));
            }
        }
    }

    private List<ManagedBoundType> GetParticipatingTypes(
        ManagedBoundType source,
        ManagedBoundType destination,
        bool includeDestinationBaseTypes)
    {
        List<ManagedBoundType> result = [];
        AddParticipatingTypeHierarchy(source, includeBaseTypes: true, result);
        AddParticipatingTypeHierarchy(
            destination, includeDestinationBaseTypes, result);
        return result;
    }

    private void AddParticipatingTypeHierarchy(
        ManagedBoundType type,
        bool includeBaseTypes,
        List<ManagedBoundType> result)
    {
        const int maximumTypes = 128;
        ManagedBoundType? current = type;
        while (current is not null)
        {
            TypeAttributes attributes = _types.GetAttributes(current);
            if ((attributes & TypeAttributes.Interface) != 0)
            {
                break;
            }

            if (!result.Any(current.IsSameType))
            {
                if (result.Count >= maximumTypes)
                {
                    throw new InvalidOperationException(
                        "User-defined conversion lookup exceeds its bounded type hierarchy.");
                }

                result.Add(current);
            }

            if (!includeBaseTypes || !current.IsReference)
            {
                break;
            }

            current = _types.GetParents(current, _thread).FirstOrDefault(parent =>
                (_types.GetAttributes(parent) & TypeAttributes.Interface) == 0);
        }
    }

    private ManagedUserDefinedConversion? SelectBest(
        List<ManagedUserDefinedConversion> matches,
        ManagedBoundType source,
        ManagedBoundType destination)
    {
        if (matches.Count <= 1)
        {
            return matches.SingleOrDefault();
        }

        ManagedBoundType? bestSource = SelectBestType(
            matches.Select(match => GetEffectiveSourceType(match, source)), source,
            mostEncompassing: false);
        if (bestSource is null)
        {
            return null;
        }

        ManagedBoundType? bestTarget = SelectBestType(
            matches.Select(match => GetEffectiveTargetType(match, destination)), destination,
            mostEncompassing: true);
        if (bestTarget is null)
        {
            return null;
        }

        ManagedUserDefinedConversion[] best = [.. matches.Where(match =>
            GetEffectiveSourceType(match, source).IsSameType(bestSource) &&
            GetEffectiveTargetType(match, destination).IsSameType(bestTarget))];
        return best.Length == 1 ? best[0] : null;
    }

    private ManagedBoundType GetEffectiveSourceType(
        ManagedUserDefinedConversion conversion,
        ManagedBoundType source) =>
        TryGetNullableUnderlying(source, out _) &&
        (conversion.IsLifted ||
            HasSupportedExplicitNullableInputConversion(source, conversion.ParameterType))
            ? _types.MakeNullable(conversion.ParameterType, _thread)
            : conversion.ParameterType;

    private ManagedBoundType GetEffectiveTargetType(
        ManagedUserDefinedConversion conversion,
        ManagedBoundType destination) => conversion.ResultType.IsSameType(destination) ||
        HasExactExplicitNullableResultConversion(conversion.ResultType, destination)
            ? destination
            : conversion.ResultType;

    private bool IsApplicableLiftedConversion(
        ManagedBoundType source,
        ManagedBoundType destination,
        ManagedBoundType parameter,
        ManagedBoundType result)
    {
        if (!HasSupportedExplicitNullableInputConversion(source, parameter))
        {
            return false;
        }

        if (TryGetNullableUnderlying(destination, out ManagedBoundType destinationUnderlying))
        {
            return result.IsSameType(destination) || result.IsSameType(destinationUnderlying) ||
                ManagedPrimitiveConversionEvaluator.IsStandardExplicitUserDefinedConversion(
                    result, destinationUnderlying, _language);
        }

        return result.IsReference && destination.IsReference &&
            _referenceConversions.IsImplicit(result, destination, _thread);
    }

    private ManagedBoundType StripNullable(ManagedBoundType type) =>
        TryGetNullableUnderlying(type, out ManagedBoundType underlying)
            ? underlying
            : type;

    private bool TryGetNullableUnderlying(
        ManagedBoundType type,
        out ManagedBoundType underlying)
    {
        if (_types.IsCoreType(type, "System.Nullable`1", _thread) &&
            type.TypeArguments is [ManagedBoundType argument])
        {
            underlying = argument;
            return true;
        }

        underlying = type;
        return false;
    }

    private ManagedBoundType? SelectBestType(
        IEnumerable<ManagedBoundType> candidates,
        ManagedBoundType exactType,
        bool mostEncompassing)
    {
        List<ManagedBoundType> distinct = [];
        foreach (ManagedBoundType candidate in candidates.Where(candidate =>
            !distinct.Any(candidate.IsSameType)))
        {
            distinct.Add(candidate);
        }

        ManagedBoundType[] exact = [.. distinct.Where(exactType.IsSameType)];
        if (exact.Length == 1)
        {
            return exact[0];
        }

        ManagedBoundType[] best = [.. distinct.Where(candidate => distinct.All(other =>
            candidate.IsSameType(other) || (mostEncompassing
                ? HasStandardImplicitConversion(other, candidate)
                : HasStandardImplicitConversion(candidate, other))))];
        return best.Length == 1 ? best[0] : null;
    }

    private bool HasStandardImplicitConversion(
        ManagedBoundType source,
        ManagedBoundType destination)
    {
        if (source.IsSameType(destination))
        {
            return true;
        }

        if (TryGetNullableUnderlying(source, out ManagedBoundType sourceUnderlying) &&
            TryGetNullableUnderlying(destination, out ManagedBoundType destinationUnderlying))
        {
            return HasStandardImplicitConversion(sourceUnderlying, destinationUnderlying);
        }

        return _referenceConversions.IsImplicit(source, destination, _thread) ||
            _referenceConversions.IsImplicitBoxing(source, destination, _thread) ||
            ManagedPrimitiveConversionEvaluator.IsImplicitInvocationConversion(
                source, destination, _language);
    }

    private bool HasSupportedStandardExplicitInputConversion(
        ManagedBoundType source,
        ManagedBoundType destination) => HasStandardImplicitConversion(source, destination) ||
        HasStandardExplicitReferenceConversion(source, destination) ||
        HasSupportedStandardUnboxingConversion(source, destination) ||
        ManagedPrimitiveConversionEvaluator.IsStandardExplicitUserDefinedConversion(
            source, destination, _language);

    private bool HasSupportedStandardExplicitResultConversion(
        ManagedBoundType source,
        ManagedBoundType destination) => source.IsSameType(destination) ||
        _referenceConversions.IsImplicit(source, destination, _thread) ||
        HasStandardExplicitReferenceConversion(source, destination) ||
        ManagedPrimitiveConversionEvaluator.IsStandardExplicitUserDefinedConversion(
            source, destination, _language);

    private bool HasSupportedExplicitNullableInputConversion(
        ManagedBoundType source,
        ManagedBoundType destination) =>
        TryGetNullableUnderlying(source, out ManagedBoundType underlying) &&
        (underlying.IsSameType(destination) ||
            ManagedPrimitiveConversionEvaluator.IsStandardExplicitUserDefinedConversion(
                underlying, destination, _language));

    private bool HasSupportedExplicitNullableResultConversion(
        ManagedBoundType source,
        ManagedBoundType destination) =>
        HasExactExplicitNullableResultConversion(source, destination) ||
        TryGetNullableUnderlying(destination, out ManagedBoundType underlying) &&
            ManagedPrimitiveConversionEvaluator.IsStandardExplicitUserDefinedConversion(
                source, underlying, _language);

    private bool HasExactExplicitNullableResultConversion(
        ManagedBoundType source,
        ManagedBoundType destination) =>
        TryGetNullableUnderlying(destination, out ManagedBoundType underlying) &&
        source.IsSameType(underlying);

    private bool HasStandardExplicitReferenceConversion(
        ManagedBoundType source,
        ManagedBoundType destination) => source.IsReference && destination.IsReference &&
        _referenceConversions.IsImplicit(destination, source, _thread);

    private bool HasSupportedStandardUnboxingConversion(
        ManagedBoundType source,
        ManagedBoundType destination) => source.IsReference && !destination.IsReference &&
        !_types.IsCoreType(destination, "System.Nullable`1", _thread) &&
        _referenceConversions.IsImplicitBoxing(destination, source, _thread);
}
