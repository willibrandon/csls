namespace Csls.Debugger;

/// <summary>
/// Validates and snapshots explicit local directories used to inspect captured application binaries.
/// </summary>
public static class DebuggerDumpBinarySearchPaths
{
    /// <summary>
    /// Gets the maximum number of explicit binary directories accepted for one dump session.
    /// </summary>
    public const int MaximumDirectories = 64;

    /// <summary>
    /// Returns an immutable, ordered snapshot of existing absolute local directories.
    /// </summary>
    /// <param name="paths">The optional caller-selected binary directories.</param>
    /// <returns>The validated directory snapshot.</returns>
    public static IReadOnlyList<string> Validate(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (paths.Count > MaximumDirectories)
        {
            throw new ArgumentException("BinarySearchPaths accepts at most 64 directories.", nameof(paths));
        }

        string[] result = new string[paths.Count];
        for (int index = 0; index < result.Length; index++)
        {
            string path = paths[index];
            if (string.IsNullOrWhiteSpace(path) || path.Length > 32768 || path.Contains('\0', StringComparison.Ordinal) ||
                !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
                path.StartsWith("//", StringComparison.Ordinal))
            {
                throw new ArgumentException("BinarySearchPaths must contain absolute local directory paths.", nameof(paths));
            }

            string fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                throw new ArgumentException($"The binary search directory does not exist: {fullPath}", nameof(paths));
            }
            result[index] = fullPath;
        }

        return Array.AsReadOnly(result);
    }
}
