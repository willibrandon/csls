using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Resolves repository-owned debugger test paths across local and deterministic CI builds.
/// </summary>
internal static class DebuggerTestEnvironment
{
    /// <summary>
    /// Finds the repository from explicit configuration, runtime directories, or caller source.
    /// </summary>
    /// <param name="sourcePath">The compiler-provided caller source path.</param>
    /// <returns>The absolute repository root.</returns>
    internal static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        string? configuredPath = Environment.GetEnvironmentVariable("CSLS_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string configuredRoot = Path.GetFullPath(configuredPath);
            if (File.Exists(Path.Join(configuredRoot, "Csls.slnx")))
            {
                return configuredRoot;
            }

            throw new DirectoryNotFoundException(
                $"CSLS_REPOSITORY_ROOT does not contain Csls.slnx: {configuredRoot}");
        }

        return FindFromDirectory(Environment.CurrentDirectory) ??
            FindFromDirectory(AppContext.BaseDirectory) ??
            FindFromDirectory(Path.GetDirectoryName(sourcePath)) ??
            throw new DirectoryNotFoundException("Could not locate the csls repository root.");
    }

    private static string? FindFromDirectory(string? startingPath)
    {
        if (string.IsNullOrWhiteSpace(startingPath))
        {
            return null;
        }

        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(Path.GetFullPath(startingPath));
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return null;
        }

        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
