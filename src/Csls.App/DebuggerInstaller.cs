using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Csls.App;

/// <summary>
/// Installs a verified Microsoft .NET debugger for the active platform.
/// </summary>
internal static class DebuggerInstaller
{
    private const string DebuggerVersion = "2-141-1";
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
                "BB0F3E33238521101B723B2C8DAEA66426913B8CC6B9DE39B92C611D6EBF67CC",
                "vsdbg-ui.exe");
        }

        if (OperatingSystem.IsWindows() && architecture is Architecture.X64 or Architecture.X86)
        {
            return CreatePackage(
                "win-x64",
                "coreclr-debug-win7-x64.zip",
                "5043FA5790848CA925B0412ABA0BD8BF0C6DE1D66CAD203FD0CCC64C755C9D52",
                "vsdbg-ui.exe");
        }

        if (OperatingSystem.IsMacOS() && architecture == Architecture.Arm64)
        {
            return CreatePackage(
                "osx-arm64",
                "coreclr-debug-osx-arm64.zip",
                "7D146354B9E86CD4EE9FE9E609BC0BC3D2F11F85B5C3F444EE6E9C07C48EAFFE",
                "vsdbg");
        }

        if (OperatingSystem.IsMacOS() && architecture == Architecture.X64)
        {
            return CreatePackage(
                "osx-x64",
                "coreclr-debug-osx-x64.zip",
                "FBFBDD59116845894731BCA59ED88EFDA391F495FA489F5FD8E60AD796AD250C",
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
                    "259E76A41CBBCDBD705FEF4890090145A4AB26DBFCEAA0D0552F018C5DFB6ECC",
                    "vsdbg")
                : CreatePackage(
                    "linux-arm64",
                    "coreclr-debug-linux-arm64.zip",
                    "7C3A6C702688A326A0E6AEB9D649B230A69049C583B677F9C415BDD8F972583A",
                    "vsdbg");
        }

        if (OperatingSystem.IsLinux() && architecture == Architecture.X64)
        {
            return isMusl
                ? CreatePackage(
                    "linux-musl-x64",
                    "coreclr-debug-linux-musl-x64.zip",
                    "1F43DEEF83C428D9ECC8FCF72F6BAF00CE7DF379944F500F4074DC0DAB1F2F78",
                    "vsdbg")
                : CreatePackage(
                    "linux-x64",
                    "coreclr-debug-linux-x64.zip",
                    "55595A399AD5D7B04815DC694E3F1F1F835B673529F72593650BEDF127E751C4",
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
            $"{DebuggerVersion}-{platform}",
            new Uri(
                "https://vsdebugger-cyg0dxb6czfafzaz.b01.azurefd.net/" +
                $"coreclr-debug-{DebuggerVersion}/{fileName}"),
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
