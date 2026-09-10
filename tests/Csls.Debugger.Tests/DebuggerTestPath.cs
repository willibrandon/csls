namespace Csls.Debugger.Tests;

/// <summary>
/// Compares debugger paths using the current file system's path semantics.
/// </summary>
internal static class DebuggerTestPath
{
    /// <summary>
    /// Determines whether two absolute paths identify the same test file.
    /// </summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    /// <returns>True when both paths identify the same file.</returns>
    internal static bool AreEquivalent(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        string normalizedLeft = Canonicalize(left).Replace('\\', '/');
        string normalizedRight = Canonicalize(right).Replace('\\', '/');
        return string.Equals(
            normalizedLeft,
            normalizedRight,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves existing symbolic-link ancestors to the host file system's canonical path.
    /// </summary>
    /// <param name="path">The path to canonicalize.</param>
    /// <returns>The absolute path with existing symbolic-link ancestors resolved.</returns>
    internal static string Canonicalize(string path)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        string root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException($"The debugger test path has no root: {fullPath}");
        string currentPath = root;
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

        return Path.TrimEndingDirectorySeparator(currentPath);
    }
}
