using Microsoft.CodeAnalysis;

namespace Csls.Workspaces;

/// <summary>
/// Tracks one file image through every live reference-property variant.
/// </summary>
internal sealed class MetadataReferenceImageCacheEntry
{
    private readonly long _imageLength;
    private readonly long _imageWriteTicks;
    private readonly long _documentationLength;
    private readonly long _documentationWriteTicks;
    private readonly Dictionary<MetadataReferenceProperties, WeakReference<PortableExecutableReference>> _references = [];

    /// <summary>
    /// Captures the assembly and documentation file identities for one image.
    /// </summary>
    internal MetadataReferenceImageCacheEntry(
        long imageLength,
        long imageWriteTicks,
        long documentationLength,
        long documentationWriteTicks)
    {
        _imageLength = imageLength;
        _imageWriteTicks = imageWriteTicks;
        _documentationLength = documentationLength;
        _documentationWriteTicks = documentationWriteTicks;
    }

    /// <summary>
    /// Reports whether both file identities still match this image.
    /// </summary>
    internal bool Matches(
        long imageLength,
        long imageWriteTicks,
        long documentationLength,
        long documentationWriteTicks) =>
        _imageLength == imageLength &&
        _imageWriteTicks == imageWriteTicks &&
        _documentationLength == documentationLength &&
        _documentationWriteTicks == documentationWriteTicks;

    /// <summary>
    /// Creates a property variant from any live reference to this image.
    /// </summary>
    internal PortableExecutableReference? GetReference(
        MetadataReferenceProperties properties,
        out bool created)
    {
        created = false;
        if (_references.TryGetValue(properties, out WeakReference<PortableExecutableReference>? exact) &&
            exact.TryGetTarget(out PortableExecutableReference? matching))
        {
            return matching;
        }

        PortableExecutableReference? source = null;
        Dictionary<MetadataReferenceProperties, WeakReference<PortableExecutableReference>>.ValueCollection.Enumerator
            candidates = _references.Values.GetEnumerator();
        while (candidates.MoveNext())
        {
            if (candidates.Current.TryGetTarget(out source))
            {
                break;
            }
        }

        if (source is null)
        {
            return null;
        }

        PortableExecutableReference reference = source.WithProperties(properties);
        Add(reference);
        created = true;
        return reference;
    }

    /// <summary>
    /// Remembers a reference without owning its lifetime.
    /// </summary>
    internal void Add(PortableExecutableReference reference) =>
        _references[reference.Properties] = new WeakReference<PortableExecutableReference>(reference);

    /// <summary>
    /// Removes collected variants and reports whether an image remains in use.
    /// </summary>
    internal bool PruneDeadReferences()
    {
        MetadataReferenceProperties[] deadProperties =
        [
            .. _references
                .Where(static pair => !pair.Value.TryGetTarget(out _))
                .Select(static pair => pair.Key)
        ];
        foreach (MetadataReferenceProperties properties in deadProperties)
        {
            _references.Remove(properties);
        }

        return _references.Count != 0;
    }
}
