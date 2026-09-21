using Microsoft.CodeAnalysis;

namespace Csls.Workspaces;

/// <summary>
/// Shares native metadata images while matching references remain in use.
/// </summary>
public static class MetadataReferenceImageCache
{
    private static readonly Lock s_gate = new();
    private static readonly Dictionary<string, (
        long ImageLength,
        long ImageWriteTicks,
        long DocumentationLength,
        long DocumentationWriteTicks,
        WeakReference<PortableExecutableReference> Reference)> s_references = new(PathComparer);
    private static int s_insertionsSinceSweep;

    /// <summary>
    /// Gets a reference to the current image while reusing its native metadata.
    /// </summary>
    /// <param name="path">The absolute assembly or module path.</param>
    /// <param name="properties">The requested metadata reference properties.</param>
    /// <returns>A reference to the current metadata image.</returns>
    public static PortableExecutableReference GetReference(
        string path,
        MetadataReferenceProperties properties)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string key = $"{path}\0{(int)properties.Kind}";
        var image = new FileInfo(path);
        long imageLength = image.Length;
        long imageWriteTicks = image.LastWriteTimeUtc.Ticks;
        string documentationPath = Path.ChangeExtension(path, ".xml");
        var documentation = new FileInfo(documentationPath);
        long documentationLength = documentation.Exists ? documentation.Length : -1;
        long documentationWriteTicks = documentation.Exists
            ? documentation.LastWriteTimeUtc.Ticks
            : -1;

        lock (s_gate)
        {
            if (s_references.TryGetValue(key, out (
                long ImageLength,
                long ImageWriteTicks,
                long DocumentationLength,
                long DocumentationWriteTicks,
                WeakReference<PortableExecutableReference> Reference) entry) &&
                entry.ImageLength == imageLength &&
                entry.ImageWriteTicks == imageWriteTicks &&
                entry.DocumentationLength == documentationLength &&
                entry.DocumentationWriteTicks == documentationWriteTicks &&
                entry.Reference.TryGetTarget(out PortableExecutableReference? cached))
            {
                return cached.WithProperties(properties);
            }

            DocumentationProvider? documentationProvider = documentation.Exists
                ? XmlDocumentationProvider.CreateFromFile(documentationPath)
                : null;
            PortableExecutableReference reference = MetadataReference.CreateFromFile(
                path,
                properties,
                documentationProvider);
            s_references[key] = (
                imageLength,
                imageWriteTicks,
                documentationLength,
                documentationWriteTicks,
                new WeakReference<PortableExecutableReference>(reference));
            if (++s_insertionsSinceSweep >= 256)
            {
                SweepDeadReferences();
                s_insertionsSinceSweep = 0;
            }

            return reference;
        }
    }

    private static void SweepDeadReferences()
    {
        string[] deadKeys =
        [
            .. s_references
                .Where(static entry => !entry.Value.Reference.TryGetTarget(out _))
                .Select(static entry => entry.Key)
        ];
        foreach (string key in deadKeys)
        {
            s_references.Remove(key);
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
