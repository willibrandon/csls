using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Resolves aggregate metadata rows and heap handles across an owned chain of minimal deltas.
/// </summary>
internal sealed class ManagedMetadataImage : IDisposable
{
    private const int MaximumMappedRows = 1_000_000;
    private readonly List<MetadataReaderProvider> _providers = [];
    private readonly List<MetadataReader> _readers;
    private readonly Dictionary<EntityHandle, (MetadataReader Reader, EntityHandle Handle)> _updatedRows = [];
    private readonly Dictionary<MethodDefinitionHandle, TypeDefinitionHandle> _methodOwners = [];
    private readonly Dictionary<TypeDefinitionHandle, List<MethodDefinitionHandle>> _addedMethods = [];
    private readonly Dictionary<MethodDefinitionHandle, List<ParameterHandle>> _addedParameters = [];
    private readonly MetadataAggregator? _aggregator;

    /// <summary>
    /// Opens immutable delta images while borrowing the baseline reader for this scope's lifetime.
    /// </summary>
    internal ManagedMetadataImage(MetadataReader baseline, IReadOnlyList<byte[]> deltas)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(deltas);
        _readers = [baseline];
        try
        {
            int mappedRows = 0;
            foreach (MetadataReaderProvider provider in deltas.Select(static image =>
                         MetadataReaderProvider.FromMetadataImage(ImmutableCollectionsMarshal.AsImmutableArray(image))))
            {
                _providers.Add(provider);
                MetadataReader reader = provider.GetMetadataReader();
                _readers.Add(reader);
                Dictionary<HandleKind, int> rowNumbers = [];
                foreach (EntityHandle aggregate in reader.GetEditAndContinueMapEntries())
                {
                    if (++mappedRows > MaximumMappedRows)
                    {
                        throw new BadImageFormatException("The metadata delta chain exceeds its mapped-row budget.");
                    }

                    int row = checked(rowNumbers.GetValueOrDefault(aggregate.Kind) + 1);
                    rowNumbers[aggregate.Kind] = row;
                    int tokenType = MetadataTokens.GetToken(aggregate) & unchecked((int)0xff000000);
                    _updatedRows[aggregate] = (reader, MetadataTokens.EntityHandle(tokenType | row));
                }

                ReadMethodRelationships(reader);
            }

            if (deltas.Count > 0)
            {
                _aggregator = new MetadataAggregator(baseline, _readers.GetRange(1, deltas.Count));
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Gets the borrowed baseline used by signature decoders whose type callbacks resolve aggregate tokens.
    /// </summary>
    internal MetadataReader Baseline => _readers[0];

    /// <summary>
    /// Identifies aggregate method definitions present in this metadata generation.
    /// </summary>
    internal bool ContainsMethod(MethodDefinitionHandle handle) => !handle.IsNil &&
        (MetadataTokens.GetRowNumber(handle) <= Baseline.MethodDefinitions.Count || _updatedRows.ContainsKey(handle));

    /// <summary>
    /// Enumerates existing and newly added methods once in aggregate metadata-token order.
    /// </summary>
    internal IEnumerable<MethodDefinitionHandle> GetMethods() => Baseline.MethodDefinitions
        .Concat(_methodOwners.Keys.OrderBy(static handle => MetadataTokens.GetRowNumber(handle)));

    /// <summary>
    /// Enumerates a type's baseline and added methods without interpreting suppressed delta member lists.
    /// </summary>
    internal IEnumerable<MethodDefinitionHandle> GetMethods(TypeDefinitionHandle handle)
    {
        IEnumerable<MethodDefinitionHandle> baseline = MetadataTokens.GetRowNumber(handle) <= Baseline.TypeDefinitions.Count
            ? Baseline.GetTypeDefinition(handle).GetMethods() : [];
        return _addedMethods.TryGetValue(handle, out List<MethodDefinitionHandle>? added)
            ? baseline.Concat(added.OrderBy(static method => MetadataTokens.GetRowNumber(method))) : baseline;
    }

    /// <summary>
    /// Reads the current method-definition row while preserving aggregate handles in its columns.
    /// </summary>
    internal MethodDefinition GetMethodDefinition(MethodDefinitionHandle handle)
    {
        if (!ContainsMethod(handle))
        {
            throw new BadImageFormatException("The method token is outside this metadata generation.");
        }

        (MetadataReader reader, EntityHandle relative) = Resolve(handle);
        return reader.GetMethodDefinition((MethodDefinitionHandle)relative);
    }

    /// <summary>
    /// Resolves method ownership using baseline member lists and explicit delta additions.
    /// </summary>
    internal TypeDefinitionHandle GetDeclaringType(MethodDefinitionHandle handle) =>
        _methodOwners.TryGetValue(handle, out TypeDefinitionHandle declaringType)
            ? declaringType : Baseline.GetMethodDefinition(handle).GetDeclaringType();

    /// <summary>
    /// Enumerates aggregate parameter handles without interpreting suppressed delta member lists.
    /// </summary>
    internal IReadOnlyList<ParameterHandle> GetParameters(MethodDefinitionHandle handle)
    {
        List<ParameterHandle> parameters = MetadataTokens.GetRowNumber(handle) <= Baseline.MethodDefinitions.Count
            ? [.. Baseline.GetMethodDefinition(handle).GetParameters()] : [];
        if (_addedParameters.TryGetValue(handle, out List<ParameterHandle>? added))
        {
            parameters.AddRange(added);
        }

        return parameters;
    }

    /// <summary>
    /// Reads the latest parameter row for an aggregate parameter handle.
    /// </summary>
    internal Parameter GetParameter(ParameterHandle handle)
    {
        (MetadataReader reader, EntityHandle relative) = Resolve(handle);
        return reader.GetParameter((ParameterHandle)relative);
    }

    /// <summary>
    /// Reads current custom attributes associated with an aggregate parent token.
    /// </summary>
    internal IReadOnlyList<CustomAttribute> GetCustomAttributes(EntityHandle parent)
    {
        List<CustomAttribute> attributes = [.. Baseline.GetCustomAttributes(parent)
            .Where(handle => !_updatedRows.ContainsKey(handle)).Select(Baseline.GetCustomAttribute)];
        foreach ((EntityHandle aggregate, (MetadataReader reader, EntityHandle relative)) in _updatedRows)
        {
            if (aggregate.Kind == HandleKind.CustomAttribute)
            {
                CustomAttribute attribute = reader.GetCustomAttribute((CustomAttributeHandle)relative);
                if (attribute.Parent == parent)
                {
                    attributes.Add(attribute);
                }
            }
        }

        return attributes;
    }

    /// <summary>
    /// Resolves the aggregate type that declares a custom-attribute constructor.
    /// </summary>
    internal EntityHandle GetAttributeType(CustomAttribute attribute)
    {
        EntityHandle constructor = attribute.Constructor;
        if (constructor.Kind == HandleKind.MethodDefinition)
        {
            return GetDeclaringType((MethodDefinitionHandle)constructor);
        }

        if (constructor.Kind == HandleKind.MemberReference)
        {
            (MetadataReader reader, EntityHandle relative) = Resolve(constructor);
            return reader.GetMemberReference((MemberReferenceHandle)relative).Parent;
        }

        throw new BadImageFormatException("A custom attribute has an invalid constructor token.");
    }

    /// <summary>
    /// Counts a type's distinct generic declarations across the metadata chain.
    /// </summary>
    internal int GetGenericParameterCount(TypeDefinitionHandle handle)
    {
        HashSet<EntityHandle> parameters = MetadataTokens.GetRowNumber(handle) <= Baseline.TypeDefinitions.Count
            ? [.. Baseline.GetTypeDefinition(handle).GetGenericParameters().Select(static parameter => (EntityHandle)parameter)] : [];
        foreach ((EntityHandle aggregate, (MetadataReader reader, EntityHandle relative)) in _updatedRows)
        {
            if (aggregate.Kind == HandleKind.GenericParameter &&
                reader.GetGenericParameter((GenericParameterHandle)relative).Parent == handle)
            {
                parameters.Add(aggregate);
            }
        }

        return parameters.Count;
    }

    /// <summary>
    /// Decodes a method signature through its owning heap and aggregate type references.
    /// </summary>
    internal MethodSignature<ManagedMetadataTypeSignature> DecodeMethodSignature(MethodDefinitionHandle handle, nint module)
    {
        BlobReader blob = GetBlobReader(GetMethodDefinition(handle).Signature);
        var provider = new ManagedMetadataTypeSignatureProvider(module, this);
        var decoder = new SignatureDecoder<ManagedMetadataTypeSignature, object?>(provider, Baseline, genericContext: null);
        return decoder.DecodeMethodSignature(ref blob);
    }

    /// <summary>
    /// Finds the most recent row for an aggregate entity token.
    /// </summary>
    internal (MetadataReader Reader, EntityHandle Handle) Resolve(EntityHandle handle) =>
        _updatedRows.TryGetValue(handle, out (MetadataReader Reader, EntityHandle Handle) updated)
            ? updated : (Baseline, handle);

    /// <summary>
    /// Reads an aggregate string handle from the heap generation that owns its bytes.
    /// </summary>
    internal string GetString(StringHandle handle)
    {
        if (handle.IsNil)
        {
            return string.Empty;
        }

        int generation = 0;
        StringHandle local = _aggregator is null ? handle : (StringHandle)_aggregator.GetGenerationHandle(handle, out generation);
        return _readers[generation].GetString(local);
    }

    /// <summary>
    /// Reads an aggregate blob handle without treating its cumulative offset as a local heap offset.
    /// </summary>
    internal BlobReader GetBlobReader(BlobHandle handle)
    {
        int generation = 0;
        BlobHandle local = _aggregator is null ? handle : (BlobHandle)_aggregator.GetGenerationHandle(handle, out generation);
        return _readers[generation].GetBlobReader(local);
    }

    /// <summary>
    /// Reads the current type-definition row for an aggregate token.
    /// </summary>
    internal TypeDefinition GetTypeDefinition(TypeDefinitionHandle handle)
    {
        (MetadataReader reader, EntityHandle relative) = Resolve(handle);
        return reader.GetTypeDefinition((TypeDefinitionHandle)relative);
    }

    /// <summary>
    /// Reads the current type-reference row for an aggregate token.
    /// </summary>
    internal TypeReference GetTypeReference(TypeReferenceHandle handle)
    {
        (MetadataReader reader, EntityHandle relative) = Resolve(handle);
        return reader.GetTypeReference((TypeReferenceHandle)relative);
    }

    /// <summary>
    /// Reads the current assembly-reference row for an aggregate token.
    /// </summary>
    internal AssemblyReference GetAssemblyReference(AssemblyReferenceHandle handle)
    {
        (MetadataReader reader, EntityHandle relative) = Resolve(handle);
        return reader.GetAssemblyReference((AssemblyReferenceHandle)relative);
    }

    /// <summary>
    /// Enumerates distinct assembly-reference tokens across the current metadata generation.
    /// </summary>
    internal IEnumerable<AssemblyReferenceHandle> GetAssemblyReferences() => Baseline.AssemblyReferences
        .Concat(_updatedRows.Keys.Where(handle => handle.Kind == HandleKind.AssemblyReference &&
            MetadataTokens.GetRowNumber(handle) > Baseline.AssemblyReferences.Count)
            .OrderBy(static handle => MetadataTokens.GetRowNumber(handle))
            .Select(static handle => (AssemblyReferenceHandle)handle));

    /// <summary>
    /// Finds a nested type's aggregate parent without confusing relative delta rows with global tokens.
    /// </summary>
    internal TypeDefinitionHandle GetDeclaringType(TypeDefinitionHandle handle)
    {
        for (int index = _readers.Count - 1; index >= 0; index--)
        {
            TypeDefinitionHandle declaring = _readers[index].GetTypeDefinition(handle).GetDeclaringType();
            if (!declaring.IsNil)
            {
                return declaring;
            }
        }

        return default;
    }

    /// <summary>
    /// Releases only delta readers; the caller retains ownership of the borrowed baseline reader.
    /// </summary>
    public void Dispose()
    {
        foreach (MetadataReaderProvider provider in _providers)
        {
            provider.Dispose();
        }

        _providers.Clear();
    }

    private void ReadMethodRelationships(MetadataReader reader)
    {
        EntityHandle parent = default;
        HandleKind expectedKind = default;
        int entries = 0;
        foreach (EditAndContinueLogEntry entry in reader.GetEditAndContinueLogEntries())
        {
            if (++entries > 2 * MaximumMappedRows)
            {
                throw new BadImageFormatException("The metadata delta exceeds its edit-log budget.");
            }

            if (!parent.IsNil)
            {
                if (entry.Operation != EditAndContinueOperation.Default || entry.Handle.Kind != expectedKind || entry.Handle.IsNil)
                {
                    throw new BadImageFormatException("A metadata addition has no matching child definition.");
                }

                if (expectedKind == HandleKind.MethodDefinition)
                {
                    var method = (MethodDefinitionHandle)entry.Handle;
                    if (MetadataTokens.GetRowNumber(method) <= Baseline.MethodDefinitions.Count ||
                        !_methodOwners.TryAdd(method, (TypeDefinitionHandle)parent))
                    {
                        throw new BadImageFormatException("A method has more than one declaring type.");
                    }

                    var declaringType = (TypeDefinitionHandle)parent;
                    if (!_addedMethods.TryGetValue(declaringType, out List<MethodDefinitionHandle>? methods))
                    {
                        methods = [];
                        _addedMethods.Add(declaringType, methods);
                    }

                    methods.Add(method);
                }
                else
                {
                    var method = (MethodDefinitionHandle)parent;
                    if (!_addedParameters.TryGetValue(method, out List<ParameterHandle>? parameters))
                    {
                        parameters = [];
                        _addedParameters.Add(method, parameters);
                    }

                    parameters.Add((ParameterHandle)entry.Handle);
                }

                parent = default;
            }
            else if (entry.Operation is EditAndContinueOperation.AddMethod or EditAndContinueOperation.AddParameter)
            {
                expectedKind = entry.Operation == EditAndContinueOperation.AddMethod
                    ? HandleKind.MethodDefinition : HandleKind.Parameter;
                HandleKind parentKind = entry.Operation == EditAndContinueOperation.AddMethod
                    ? HandleKind.TypeDefinition : HandleKind.MethodDefinition;
                if (entry.Handle.IsNil || entry.Handle.Kind != parentKind)
                {
                    throw new BadImageFormatException("A metadata addition has an invalid parent definition.");
                }

                parent = entry.Handle;
            }
        }

        if (!parent.IsNil)
        {
            throw new BadImageFormatException("The metadata edit log ends before its child definition.");
        }
    }
}
