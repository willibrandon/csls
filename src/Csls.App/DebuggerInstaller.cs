using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Csls.App;

/// <summary>
/// Installs a verified Microsoft .NET debugger for the active platform.
/// </summary>
internal static class DebuggerInstaller
{
    private const string DebuggerDownloadRoot =
        "https://download.visualstudio.microsoft.com/download/pr/" +
        "6656678b-5409-42ef-990e-c4f3cd7b5f5a/";
    private static readonly HttpClient s_httpClient = new();

    /// <summary>
    /// Installs the debugger and writes its executable path to standard output.
    /// </summary>
    /// <param name="outputRoot">The private debugger storage directory.</param>
    /// <param name="archivePath">An optional previously downloaded debugger archive.</param>
    /// <param name="cancellationToken">Signals that installation should stop.</param>
    /// <returns>Zero on success; otherwise, one.</returns>
    internal static async Task<int> InstallAsync(
        string outputRoot,
        string? archivePath,
        CancellationToken cancellationToken)
    {
        try
        {
            DebuggerPackage package = ResolvePackage();
            string normalizedOutputRoot = Path.GetFullPath(outputRoot);
            Directory.CreateDirectory(normalizedOutputRoot);
            string installationPath = Path.Join(normalizedOutputRoot, package.Identifier);
            string executablePath = Path.Join(installationPath, package.ExecutableName);
            string markerPath = Path.Join(installationPath, "csls.install.complete");
            if (await IsCompleteAsync(
                executablePath,
                markerPath,
                package.Sha256,
                cancellationToken).ConfigureAwait(false))
            {
                await Console.Out.WriteLineAsync(executablePath).ConfigureAwait(false);
                return 0;
            }

            string temporaryRoot = Path.Join(
                normalizedOutputRoot,
                $".{package.Identifier}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryRoot);
            string packagePath = archivePath is null
                ? Path.Join(temporaryRoot, "debugger.zip")
                : Path.GetFullPath(archivePath);
            try
            {
                if (archivePath is null)
                {
                    await DownloadAsync(package.Source, packagePath, cancellationToken)
                        .ConfigureAwait(false);
                }

                await VerifyAsync(packagePath, package.Sha256, cancellationToken)
                    .ConfigureAwait(false);
                string extractedPath = Path.Join(temporaryRoot, "extracted");
                await ExtractAsync(packagePath, extractedPath, cancellationToken)
                    .ConfigureAwait(false);
                string extractedExecutable = Path.Join(extractedPath, package.ExecutableName);
                if (!File.Exists(extractedExecutable))
                {
                    throw new InvalidDataException(
                        $"The debugger package does not contain {package.ExecutableName}.");
                }

                MakeExecutable(extractedExecutable);
                await File.WriteAllTextAsync(
                    Path.Join(extractedPath, "csls.install.complete"),
                    package.Sha256,
                    cancellationToken).ConfigureAwait(false);
                ReplaceInstallation(extractedPath, installationPath);
            }
            finally
            {
                if (Directory.Exists(temporaryRoot))
                {
                    Directory.Delete(temporaryRoot, recursive: true);
                }
            }

            await Console.Out.WriteLineAsync(executablePath).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is
            HttpRequestException or
            IOException or
            InvalidDataException or
            PlatformNotSupportedException or
            UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task DownloadAsync(
        Uri source,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await s_httpClient.GetAsync(
            source,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using Stream sourceStream = await response.Content.ReadAsStreamAsync(
            cancellationToken).ConfigureAwait(false);
        using var destinationStream = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 131_072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExtractAsync(
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
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string entryPath = Path.GetFullPath(Path.Join(
                destinationPath,
                entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!entryPath.StartsWith(normalizedDestination, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The debugger package contains an unsafe path: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(entryPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
            using Stream entryStream = await entry.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            using var destinationStream = new FileStream(
                entryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 131_072,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await entryStream.CopyToAsync(destinationStream, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<bool> IsCompleteAsync(
        string executablePath,
        string markerPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(executablePath) || !File.Exists(markerPath))
        {
            return false;
        }

        string marker = await File.ReadAllTextAsync(markerPath, cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(marker, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void MakeExecutable(string executablePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        UnixFileMode mode = File.GetUnixFileMode(executablePath);
        File.SetUnixFileMode(
            executablePath,
            mode |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherExecute);
    }

    private static void ReplaceInstallation(string sourcePath, string destinationPath)
    {
        string? previousPath = null;
        try
        {
            if (Directory.Exists(destinationPath))
            {
                previousPath = $"{destinationPath}.previous-{Guid.NewGuid():N}";
                Directory.Move(destinationPath, previousPath);
            }

            Directory.Move(sourcePath, destinationPath);
            if (previousPath is not null)
            {
                Directory.Delete(previousPath, recursive: true);
            }
        }
        catch
        {
            if (!Directory.Exists(destinationPath) &&
                previousPath is not null &&
                Directory.Exists(previousPath))
            {
                Directory.Move(previousPath, destinationPath);
            }

            throw;
        }
    }

    private static DebuggerPackage ResolvePackage()
    {
        Architecture architecture = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsWindows() && architecture == Architecture.Arm64)
        {
            return CreatePackage(
                "win-arm64",
                "coreclr-debug-win10-arm64.zip",
                "EFD91A8EBE490C154AA237E7888DE9F6019FEAB943AD4DDBFE3CA846EE8E6544",
                "vsdbg-ui.exe");
        }

        if (OperatingSystem.IsWindows() && architecture == Architecture.X64)
        {
            return CreatePackage(
                "win-x64",
                "coreclr-debug-win7-x64.zip",
                "8C8AA11A7628875DA1E502455AB1C086803F5E02BFF4B620D921B117B7BDCED5",
                "vsdbg-ui.exe");
        }

        if (OperatingSystem.IsMacOS() && architecture == Architecture.Arm64)
        {
            return CreatePackage(
                "osx-arm64",
                "coreclr-debug-osx-arm64.zip",
                "11AE64045BFB087F653DC2296DA5E436971C24DFA0583C6B7F63D037659FB832",
                "vsdbg");
        }

        if (OperatingSystem.IsMacOS() && architecture == Architecture.X64)
        {
            return CreatePackage(
                "osx-x64",
                "coreclr-debug-osx-x64.zip",
                "7E83CD507C3566F3CF0DE54EED71A5197476D2373F7BDF67488B8601A1383EF8",
                "vsdbg");
        }

        bool isMusl = RuntimeInformation.RuntimeIdentifier.Contains(
            "linux-musl",
            StringComparison.Ordinal) || File.Exists("/etc/alpine-release");
        if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64)
        {
            return isMusl
                ? CreatePackage(
                    "linux-musl-arm64",
                    "coreclr-debug-linux-musl-arm64.zip",
                    "F101964618437ADE5C8A9438C8C2A59E0149399A1CD468684FB2A1349137B5A6",
                    "vsdbg")
                : CreatePackage(
                    "linux-arm64",
                    "coreclr-debug-linux-arm64.zip",
                    "46D51AFD9629A2480560209D83D2524655ABE86E27A20FDCE230B69729440B1D",
                    "vsdbg");
        }

        if (OperatingSystem.IsLinux() && architecture == Architecture.X64)
        {
            return isMusl
                ? CreatePackage(
                    "linux-musl-x64",
                    "coreclr-debug-linux-musl-x64.zip",
                    "B4E2AE93C40FD39FB045626395BFDEDDD4885A0F122EA75D0B955092C2977BBB",
                    "vsdbg")
                : CreatePackage(
                    "linux-x64",
                    "coreclr-debug-linux-x64.zip",
                    "C504D062DC09C15FC7C0329147BEAA39117A3CBC4B3D9B0724B65265D1BF25E1",
                    "vsdbg");
        }

        throw new PlatformNotSupportedException(
            $"The Microsoft .NET debugger is unavailable for " +
            $"{RuntimeInformation.OSDescription} {architecture}.");
    }

    private static DebuggerPackage CreatePackage(
        string platform,
        string fileName,
        string sha256,
        string executableName) => new(
            $"{platform}-{sha256[..12]}",
            new Uri(DebuggerDownloadRoot + fileName),
            sha256,
            executableName);

    private static async Task VerifyAsync(
        string packagePath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 131_072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        string actualSha256 = Convert.ToHexString(digest);
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The debugger package SHA-256 was {actualSha256}; " +
                $"expected {expectedSha256}.");
        }
    }
}
