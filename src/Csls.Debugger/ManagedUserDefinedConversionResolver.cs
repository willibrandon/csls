using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves exact implicit conversion operators from the current loaded metadata generation.
/// </summary>
internal sealed class ManagedUserDefinedConversionResolver
{
    private readonly ManagedBoundTypeSystem _types;
    private readonly nint _thread;
    private readonly List<(ManagedBoundType Source, ManagedBoundType Destination,
        ManagedUserDefinedConversion? Result)> _cache = [];

    /// <summary>
    /// Creates a resolver scoped to one stopped managed thread and loaded type universe.
    /// </summary>
    /// <param name="types">The exact loaded type system.</param>
    /// <param name="thread">The borrowed managed thread used for core-library identity.</param>
    internal ManagedUserDefinedConversionResolver(ManagedBoundTypeSystem types, nint thread)
    {
        ArgumentNullException.ThrowIfNull(types);
        _types = types;
        _thread = thread;
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

        foreach ((ManagedBoundType cachedSource, ManagedBoundType cachedDestination,
            ManagedUserDefinedConversion? result) in _cache)
        {
            if (cachedSource.IsSameType(source) && cachedDestination.IsSameType(destination))
            {
                return result;
            }
        }

        var matches = new List<ManagedUserDefinedConversion>();
        AddMatches(source, source, destination, matches);
        if (!source.IsSameType(destination))
        {
            AddMatches(destination, source, destination, matches);
        }

        ManagedUserDefinedConversion? resolved = matches.Count == 1 ? matches[0] : null;
        _cache.Add((source, destination, resolved));
        return resolved;
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
            if (parameterType.IsSameType(source) && resultType.IsSameType(destination))
            {
                matches.Add(new ManagedUserDefinedConversion(
                    declaringType,
                    checked((uint)MetadataTokens.GetToken(handle)),
                    parameterType,
                    resultType));
            }
        }
    }
}
