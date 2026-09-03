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

        string normalizedLeft = Path.GetFullPath(left).Replace('\\', '/');
        string normalizedRight = Path.GetFullPath(right).Replace('\\', '/');
        return string.Equals(
            normalizedLeft,
            normalizedRight,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }
}
