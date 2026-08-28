using System.IO.Compression;
using System.Runtime.CompilerServices;

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
        var packageStream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 131_072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using ConfiguredAsyncDisposable packageCleanup =
            packageStream.ConfigureAwait(false);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith(DebuggerPrefix, StringComparison.Ordinal) ||
                string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            string relativePath = entry.FullName[DebuggerPrefix.Length..]
                .Replace('/', Path.DirectorySeparatorChar);
            string extractedPath = Path.GetFullPath(Path.Join(destinationPath, relativePath));
            if (!extractedPath.StartsWith(normalizedDestination, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The VSIX contains an unsafe debugger path: {entry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(extractedPath)!);
            Stream entryStream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable entryCleanup =
                entryStream.ConfigureAwait(false);
            var destinationStream = new FileStream(
                extractedPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 131_072,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using ConfiguredAsyncDisposable destinationCleanup =
                destinationStream.ConfigureAwait(false);
            await entryStream.CopyToAsync(destinationStream, cancellationToken)
                .ConfigureAwait(false);
        }

        string executableName = OperatingSystem.IsWindows() ? "vsdbg-ui.exe" : "vsdbg";
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
}
