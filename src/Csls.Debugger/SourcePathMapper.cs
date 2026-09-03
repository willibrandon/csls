namespace Csls.Debugger;

/// <summary>
/// Applies deterministic longest-prefix mappings to Portable PDB document paths.
/// </summary>
internal sealed class SourcePathMapper
{
    private readonly List<SourcePathMapping> _mappings = [];

    /// <summary>
    /// Replaces all build-time to local source path mappings.
    /// </summary>
    /// <param name="mappings">The complete mapping dictionary.</param>
    internal void Set(IReadOnlyDictionary<string, string> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        _mappings.Clear();
        foreach ((string buildPath, string localPath) in mappings)
        {
            if (!IsAbsolutePath(buildPath) ||
                !IsAbsolutePath(localPath))
            {
                throw new ArgumentException(
                    "Source path mapping keys and values must be absolute paths.",
                    nameof(mappings));
            }

            _mappings.Add(new SourcePathMapping
            {
                BuildPath = NormalizePortablePrefix(buildPath),
                LocalPath = NormalizePortablePrefix(localPath),
                Comparison = IsWindowsPath(buildPath)
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            });
        }

        _mappings.Sort(static (left, right) =>
            right.BuildPath.Length.CompareTo(left.BuildPath.Length));
    }

    /// <summary>
    /// Maps one build-time document path through the most-specific matching prefix.
    /// </summary>
    /// <param name="path">The absolute Portable PDB document path.</param>
    /// <returns>The normalized mapped path, or the original normalized path.</returns>
    internal string Map(string path)
    {
        string normalized = NormalizePath(path);
        foreach (SourcePathMapping mapping in _mappings)
        {
            if (!HasPrefix(normalized, mapping.BuildPath, mapping.Comparison))
            {
                continue;
            }

            string remainder = normalized[mapping.BuildPath.Length..].TrimStart('/');
            if (remainder.Split('/').Any(static segment => segment == ".."))
            {
                continue;
            }

            return remainder.Length == 0
                ? mapping.LocalPath
                : $"{mapping.LocalPath.TrimEnd('/')}/{remainder}";
        }

        return normalized;
    }

    private static bool HasPrefix(
        string path,
        string prefix,
        StringComparison comparison)
    {
        if (!path.StartsWith(prefix, comparison))
        {
            return false;
        }

        return path.Length == prefix.Length ||
            prefix[^1] == '/' ||
            path[prefix.Length] == '/';
    }

    /// <summary>
    /// Determines whether a path is absolute in POSIX, drive-letter, or UNC form.
    /// </summary>
    /// <param name="path">The potentially cross-platform path.</param>
    /// <returns>True when the path is absolute on its originating platform.</returns>
    internal static bool IsAbsolutePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (path[0] == '/' ||
            path.StartsWith("\\\\", StringComparison.Ordinal) ||
            IsWindowsDrivePath(path));

    private static bool IsWindowsPath(string path) =>
        IsWindowsDrivePath(path) || path.StartsWith("\\\\", StringComparison.Ordinal);

    private static bool IsWindowsDrivePath(string path) =>
        path.Length >= 3 &&
        char.IsAsciiLetter(path[0]) &&
        path[1] == ':' &&
        path[2] is '/' or '\\';

    private static string NormalizePortablePrefix(string path)
    {
        string normalized = NormalizePath(path);
        return normalized is "/" ||
            normalized.Length == 3 && normalized[1] == ':' && normalized[2] == '/'
            ? normalized
            : normalized.TrimEnd('/');
    }

    /// <summary>
    /// Normalizes cross-platform path separators without host-dependent rooting.
    /// </summary>
    /// <param name="path">The potentially cross-platform path.</param>
    /// <returns>The slash-normalized path.</returns>
    internal static string NormalizePath(string path) =>
        path.Replace('\\', '/');
}
