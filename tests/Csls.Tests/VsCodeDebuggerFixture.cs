using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Csls.Tests;

/// <summary>
/// Extracts the real Microsoft debugger already provisioned for VS Code oracle tests.
/// </summary>
internal static class VsCodeDebuggerFixture
{
    private const string DebuggerPrefix = "extension/.debugger/";

    /// <summary>
    /// Extracts the platform debugger from a verified Microsoft C# VSIX.
    /// </summary>
    /// <param name="packagePath">The verified platform-specific C# VSIX.</param>
    /// <param name="destinationPath">The isolated extraction directory.</param>
    /// <param name="cancellationToken">Signals that extraction should stop.</param>
    /// <returns>The absolute debugger executable path.</returns>
    internal static async Task<string> ExtractAsync(
        string packagePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationPath);
        string normalizedDestination = Path.GetFullPath(destinationPath) +
            Path.DirectorySeparatorChar;
        using var packageStream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 131_072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read);
        string executableName = OperatingSystem.IsWindows() ? "vsdbg-ui.exe" : "vsdbg";
        string debuggerDirectory = SelectDebuggerDirectory(archive, executableName);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith(debuggerDirectory, StringComparison.Ordinal) ||
                string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            string relativePath = entry.FullName[debuggerDirectory.Length..]
                .Replace('/', Path.DirectorySeparatorChar);
            string extractedPath = Path.GetFullPath(Path.Join(destinationPath, relativePath));
            if (!extractedPath.StartsWith(normalizedDestination, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The VSIX contains an unsafe debugger path: {entry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(extractedPath)!);
            using Stream entryStream = await entry.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            using var destinationStream = new FileStream(
                extractedPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 131_072,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await entryStream.CopyToAsync(destinationStream, cancellationToken)
                .ConfigureAwait(false);
        }

        string[] executables = [.. Directory.EnumerateFiles(
            destinationPath,
            executableName,
            SearchOption.AllDirectories)];
        if (executables.Length != 1)
        {
            throw new InvalidDataException(
                $"The VSIX contains {executables.Length} {executableName} files.");
        }

        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(executables[0]);
            File.SetUnixFileMode(
                executables[0],
                mode |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherExecute);
        }

        return executables[0];
    }

    private static string SelectDebuggerDirectory(
        ZipArchive archive,
        string executableName)
    {
        ZipArchiveEntry[] candidates =
        [
            .. archive.Entries.Where(entry =>
                string.Equals(entry.Name, executableName, StringComparison.Ordinal) &&
                entry.FullName.StartsWith(DebuggerPrefix, StringComparison.Ordinal))
        ];
        if (candidates.Length == 1)
        {
            return candidates[0].FullName[..^executableName.Length];
        }

        string architectureDirectory = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm => "arm",
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x86_64",
            Architecture.X86 => "x86",
            _ => throw new PlatformNotSupportedException(
                $"The debugger fixture does not support " +
                $"{RuntimeInformation.OSArchitecture}.")
        };
        string expectedPath = $"{DebuggerPrefix}{architectureDirectory}/{executableName}";
        ZipArchiveEntry? selected = candidates.SingleOrDefault(entry =>
            string.Equals(entry.FullName, expectedPath, StringComparison.Ordinal)) ?? throw new InvalidDataException(
                $"The VSIX contains {candidates.Length} {executableName} files but none " +
                $"for {RuntimeInformation.OSArchitecture}.");
        return selected.FullName[..^executableName.Length];
    }
}
