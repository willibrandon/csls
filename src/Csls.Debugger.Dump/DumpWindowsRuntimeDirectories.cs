using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.Debugger.Dump;

/// <summary>
/// Discovers bounded Windows runtime directories using the executing runtime and host installation settings.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DumpWindowsRuntimeDirectories
{
    private const int MaximumDirectories = 1024;

    /// <summary>
    /// Gets local runtime directories without using paths supplied by a dump or accessing symbol servers.
    /// </summary>
    /// <returns>The unique absolute runtime directories in discovery order.</returns>
    internal static IReadOnlyList<string> GetDirectories()
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string current = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        AddDirectory(directories, current);
        string? parent = Path.GetDirectoryName(current);
        if (string.Equals(Path.GetFileName(parent), "Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase))
        {
            AddFrameworkVersions(directories, parent);
        }

        string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();
        AddInstallation(directories, Environment.GetEnvironmentVariable($"DOTNET_ROOT_{architecture}"));
        if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
        {
            AddInstallation(directories, Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)"));
        }
        AddInstallation(directories, Environment.GetEnvironmentVariable("DOTNET_ROOT"));

        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using RegistryKey? installation = machine.OpenSubKey($"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\{architecture}");
        AddInstallation(directories, installation?.GetValue("InstallLocation") as string);
        return [.. directories];
    }

    private static void AddInstallation(HashSet<string> directories, string? root)
    {
        if (!string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root))
        {
            AddFrameworkVersions(directories, Path.Join(root, "shared", "Microsoft.NETCore.App"));
        }
    }

    private static void AddFrameworkVersions(HashSet<string> directories, string? framework)
    {
        if (framework is not null && Directory.Exists(framework))
        {
            foreach (string directory in Directory.EnumerateDirectories(framework))
            {
                AddDirectory(directories, directory);
            }
        }
    }

    private static void AddDirectory(HashSet<string> directories, string directory)
    {
        _ = directories.Add(Path.GetFullPath(directory));
        if (directories.Count > MaximumDirectories)
        {
            throw new InvalidDataException($"Local runtime discovery exceeds the {MaximumDirectories}-directory limit.");
        }
    }
}
