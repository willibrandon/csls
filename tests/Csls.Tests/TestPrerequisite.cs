using System.Diagnostics.CodeAnalysis;

namespace Csls.Tests;

/// <summary>
/// Skips integration tests whose optional external prerequisites are unavailable.
/// </summary>
internal static class TestPrerequisite
{
    /// <summary>
    /// Skips the current test because an optional external prerequisite is unavailable.
    /// </summary>
    /// <param name="description">The unavailable prerequisite description.</param>
    [DoesNotReturn]
    internal static void Skip(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Assert.Inconclusive($"Optional integration skipped: {description}");
        throw new InvalidOperationException("MSTest did not stop an inconclusive test.");
    }

    /// <summary>
    /// Requires an optional external file for the current integration test.
    /// </summary>
    /// <param name="path">The required absolute file path.</param>
    /// <param name="description">The prerequisite description.</param>
    internal static void RequireFile(string path, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (!File.Exists(path))
        {
            Skip(description);
        }
    }

    /// <summary>
    /// Requires an optional external command on the current process search path.
    /// </summary>
    /// <param name="commandName">The executable command name.</param>
    /// <param name="description">The prerequisite description.</param>
    internal static void RequireCommand(string commandName, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        string executableName = OperatingSystem.IsWindows() &&
            string.IsNullOrEmpty(Path.GetExtension(commandName))
                ? $"{commandName}.exe"
                : commandName;
        bool isAvailable = Path.IsPathFullyQualified(executableName)
            ? File.Exists(executableName)
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(
                    Path.PathSeparator,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(directory => File.Exists(Path.Join(directory.Trim('"'), executableName)));
        if (!isAvailable)
        {
            Skip(description);
        }
    }
}
