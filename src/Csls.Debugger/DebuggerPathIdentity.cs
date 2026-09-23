namespace Csls.Debugger;

/// <summary>
/// Compares debugger paths by their host file-system identity.
/// </summary>
internal static class DebuggerPathIdentity
{
    /// <summary>
    /// Determines whether two paths identify the same host file-system entry.
    /// </summary>
    /// <param name="left">The first normalized path.</param>
    /// <param name="right">The second normalized path.</param>
    /// <returns>True when the paths are equal or resolve through equivalent symbolic links.</returns>
    internal static bool AreEquivalent(string left, string right)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(left, right, comparison))
        {
            return true;
        }

        if (OperatingSystem.IsWindows() ||
            !Path.IsPathFullyQualified(left) ||
            !Path.IsPathFullyQualified(right))
        {
            return false;
        }

        return string.Equals(
            ResolveLinks(left),
            ResolveLinks(right),
            StringComparison.Ordinal);
    }

    private static string ResolveLinks(string path)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException($"The debugger path has no root: {fullPath}");
        string currentPath = root;
        try
        {
            foreach (string segment in fullPath[root.Length..].Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries))
            {
                currentPath = Path.Join(currentPath, segment);
                var entry = new DirectoryInfo(currentPath);
                if (entry.LinkTarget is not null)
                {
                    currentPath = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                        ?? currentPath;
                }
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return fullPath;
        }

        return Path.TrimEndingDirectorySeparator(currentPath);
    }
}
