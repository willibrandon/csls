using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Csls.Debugger.Dump;

/// <summary>
/// Discovers bounded Unix runtime installations from host settings and registered installation files.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class DumpUnixRuntimeDirectories
{
    private const int MaximumDirectories = 1024;
    private const int MaximumRegistrationBytes = 4096;

    /// <summary>
    /// Gets local runtime directories independently of paths recorded in a captured process.
    /// </summary>
    /// <returns>The unique absolute runtime directories in host discovery order.</returns>
    internal static IReadOnlyList<string> GetDirectories()
    {
        string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();
        string defaultRoot = OperatingSystem.IsMacOS() ? "/usr/local/share/dotnet" : "/usr/share/dotnet";
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
            RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            defaultRoot = Path.Join(defaultRoot, "x64");
        }

        return GetDirectories(RuntimeEnvironment.GetRuntimeDirectory(),
            Environment.GetEnvironmentVariable($"DOTNET_ROOT_{architecture}"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT"), "/etc/dotnet", defaultRoot);
    }

    /// <summary>
    /// Enumerates installed framework versions from one captured set of host installation settings.
    /// </summary>
    /// <param name="currentRuntime">The executing runtime's absolute directory.</param>
    /// <param name="architectureRoot">The architecture-specific host installation root.</param>
    /// <param name="generalRoot">The architecture-independent host installation root.</param>
    /// <param name="registrationDirectory">The directory containing the host's installation registration files.</param>
    /// <param name="defaultRoot">The platform's default installation root.</param>
    /// <returns>The unique absolute runtime directories, bounded across all roots.</returns>
    internal static IReadOnlyList<string> GetDirectories(string currentRuntime, string? architectureRoot,
        string? generalRoot, string registrationDirectory, string defaultRoot)
    {
        List<string> directories = [];
        var seenDirectories = new HashSet<string>(StringComparer.Ordinal);
        var seenFrameworks = new HashSet<string>(StringComparer.Ordinal);
        string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentRuntime));
        AddDirectory(current);
        string? framework = Path.GetDirectoryName(current);
        if (framework is not null && Path.GetFileName(framework) == "Microsoft.NETCore.App")
        {
            AddFramework(framework);
        }

        AddInstallation(architectureRoot);
        AddInstallation(generalRoot);
        AddInstallation(ReadRegisteredRoot(registrationDirectory));
        AddInstallation(defaultRoot);
        return directories;

        void AddInstallation(string? root)
        {
            if (IsAbsolutePath(root))
            {
                AddFramework(Path.Join(root, "shared", "Microsoft.NETCore.App"));
            }
        }

        void AddFramework(string directory)
        {
            directory = Path.GetFullPath(directory);
            if (!seenFrameworks.Add(directory) || !Directory.Exists(directory))
            {
                return;
            }

            try
            {
                foreach (string version in Directory.EnumerateDirectories(directory))
                {
                    AddDirectory(version);
                }
            }
            catch (DirectoryNotFoundException)
            {
                // Installation removal may race discovery; later roots remain independently useful.
                return;
            }
            catch (UnauthorizedAccessException)
            {
                // A locally installed runtime may be visible only to its owning account.
                return;
            }
        }

        void AddDirectory(string directory)
        {
            directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (seenDirectories.Add(directory))
            {
                if (directories.Count == MaximumDirectories)
                {
                    throw new InvalidDataException($"Local runtime discovery exceeds the {MaximumDirectories}-directory limit.");
                }
                directories.Add(directory);
            }
        }
    }

    private static string? ReadRegisteredRoot(string directory)
    {
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => throw new PlatformNotSupportedException("The host architecture has no supported Unix runtime installation layout.")
        };
        string? root = ReadRegistration(Path.Join(directory, $"install_location_{architecture}"), out bool exists);
        return exists ? root : ReadRegistration(Path.Join(directory, "install_location"), out _);
    }

    private static string? ReadRegistration(string path, out bool exists)
    {
        exists = true;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            {
                return null;
            }
            using FileStream stream = DebuggerInputFile.OpenRead(path);
            if (!stream.CanSeek || stream.Length > MaximumRegistrationBytes)
            {
                return null;
            }
            Span<byte> bytes = stackalloc byte[MaximumRegistrationBytes + 1];
            int count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (count > MaximumRegistrationBytes)
            {
                return null;
            }
            ReadOnlySpan<byte> content = bytes[..count];
            int newline = content.IndexOf((byte)'\n');
            string root = new UTF8Encoding(false, true).GetString(newline >= 0 ? content[..newline] : content);
            return IsAbsolutePath(root) ? root : null;
        }
        catch (FileNotFoundException)
        {
            exists = false;
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            exists = false;
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool IsAbsolutePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !path.Contains('\0', StringComparison.Ordinal) && Path.IsPathFullyQualified(path);
}
