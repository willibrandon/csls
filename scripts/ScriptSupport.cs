using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Xml.Linq;
using SharpCompress.Compressors.Xz;

/// <summary>
/// Provides shared infrastructure for csls file-based repository applications.
/// </summary>
internal static class ScriptSupport
{
    /// <summary>
    /// Finds the csls repository root from the including file-based application.
    /// </summary>
    /// <param name="sourceFilePath">The compiler-provided source file path.</param>
    /// <returns>The absolute repository root.</returns>
    internal static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "")
    {
        DirectoryInfo? directory = new(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The csls repository root was not found.");
    }

    /// <summary>
    /// Resolves the tool installation root from an explicit path, environment, or repository default.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <param name="explicitOutputPath">An optional command-line output path.</param>
    /// <returns>The absolute tool installation root.</returns>
    internal static string ResolveToolsRoot(
        string repositoryRoot,
        string? explicitOutputPath = null)
    {
        string? configuredPath = !string.IsNullOrWhiteSpace(explicitOutputPath)
            ? explicitOutputPath
            : Environment.GetEnvironmentVariable("CSLS_TOOLS_ROOT");
        return string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Join(repositoryRoot, "artifacts", "tools")
            : Path.GetFullPath(configuredPath);
    }

    /// <summary>
    /// Downloads a file and rejects it unless its SHA-256 digest matches the pin.
    /// </summary>
    /// <param name="source">The source URI.</param>
    /// <param name="destinationPath">The destination file path.</param>
    /// <param name="expectedSha256">The expected hexadecimal SHA-256 digest.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task that completes after verification succeeds.</returns>
    internal static async Task DownloadVerifiedFileAsync(
        Uri source,
        string destinationPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CheckCertificateRevocationList = !OperatingSystem.IsMacOS()
        };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("csls-tool-provisioner/0.1");
        const int maximumAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using HttpResponseMessage response = await client
                    .GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                using (FileStream destination = new(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 131_072,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await response.Content
                        .CopyToAsync(destination, cancellationToken)
                        .ConfigureAwait(false);
                }

                string actualSha256 = await ComputeSha256Async(
                    destinationPath,
                    cancellationToken).ConfigureAwait(false);
                if (!string.Equals(
                    actualSha256,
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"SHA-256 mismatch for {source}: expected {expectedSha256}, " +
                        $"got {actualSha256}.");
                }

                return;
            }
            catch (Exception exception) when (
                attempt < maximumAttempts &&
                exception is HttpRequestException or IOException)
            {
                File.Delete(destinationPath);
                await Console.Error.WriteLineAsync(
                    $"Download attempt {attempt} failed for {source}: " +
                    exception.GetBaseException().Message).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Computes the lowercase hexadecimal SHA-256 digest of a file.
    /// </summary>
    /// <param name="path">The input file path.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The lowercase hexadecimal digest.</returns>
    internal static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        using (FileStream input = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 131_072,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            byte[] digest = await SHA256.HashDataAsync(input, cancellationToken)
                .ConfigureAwait(false);
            return Convert.ToHexStringLower(digest);
        }
    }

    /// <summary>
    /// Extracts a ZIP, TAR.GZ, or TAR.XZ archive through managed .NET streams.
    /// </summary>
    /// <param name="archivePath">The verified archive path.</param>
    /// <param name="destinationPath">The extraction directory.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task that completes after extraction succeeds.</returns>
    internal static async Task ExtractArchiveAsync(
        string archivePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await ZipFile.ExtractToDirectoryAsync(
                archivePath,
                destinationPath,
                overwriteFiles: false,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        using FileStream archive = File.OpenRead(archivePath);
        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            using GZipStream decompressedArchive = new(archive, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(
                decompressedArchive,
                destinationPath,
                overwriteFiles: false,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (archivePath.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase))
        {
            using XZStream decompressedArchive = new(archive);
            await TarFile.ExtractToDirectoryAsync(
                decompressedArchive,
                destinationPath,
                overwriteFiles: false,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new InvalidDataException($"Unsupported tool archive: {archivePath}");
    }

    /// <summary>
    /// Grants normal executable permissions to a provisioned Unix tool binary.
    /// </summary>
    /// <param name="executablePath">The executable file path.</param>
    internal static void EnsureExecutable(string executablePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            executablePath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherExecute);
    }

    /// <summary>
    /// Starts a provisioned tool and verifies its version output and exit code.
    /// </summary>
    /// <param name="executablePath">The executable file path.</param>
    /// <param name="arguments">The version command arguments.</param>
    /// <param name="expectedText">Text required in combined output.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task that completes after the expected version is observed.</returns>
    internal static async Task VerifyToolAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string expectedText,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"The provisioned tool did not start: {executablePath}");
        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string output = await standardOutputTask.ConfigureAwait(false) +
            await standardErrorTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || !output.Contains(expectedText, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Expected '{expectedText}' from '{executablePath}', but it returned " +
                $"exit code {process.ExitCode}: {output.Trim()}");
        }
    }

    /// <summary>
    /// Provisions and validates a pinned archive-distributed development tool.
    /// </summary>
    /// <param name="toolsRoot">The repository tool artifact root.</param>
    /// <param name="toolName">The stable tool directory name.</param>
    /// <param name="version">The pinned tool version.</param>
    /// <param name="platform">The stable target platform name.</param>
    /// <param name="source">The official release asset URI.</param>
    /// <param name="assetName">The release asset file name.</param>
    /// <param name="expectedSha256">The expected release asset digest.</param>
    /// <param name="executableName">The executable file name in the archive.</param>
    /// <param name="installationRootLevels">Parent levels from the executable to the installation root.</param>
    /// <param name="versionArguments">Arguments used to query the tool version.</param>
    /// <param name="expectedVersionText">Text required in version output.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The absolute provisioned executable path.</returns>
    internal static async Task<string> ProvisionArchiveToolAsync(
        string toolsRoot,
        string toolName,
        string version,
        string platform,
        Uri source,
        string assetName,
        string expectedSha256,
        string executableName,
        int installationRootLevels,
        IReadOnlyList<string> versionArguments,
        string expectedVersionText,
        CancellationToken cancellationToken)
    {
        string installationPath = Path.Join(toolsRoot, toolName, version, platform);
        string executablePath = FindInstalledExecutable(
            installationPath,
            executableName);
        if (File.Exists(executablePath))
        {
            await VerifyToolAsync(
                executablePath,
                versionArguments,
                expectedVersionText,
                cancellationToken).ConfigureAwait(false);
            return executablePath;
        }

        string stagingRoot = Path.Join(
            toolsRoot,
            ".staging",
            $"{toolName}-{version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        try
        {
            string archivePath = Path.Join(stagingRoot, assetName);
            string extractionPath = Path.Join(stagingRoot, "extracted");
            Directory.CreateDirectory(extractionPath);
            await Console.Error.WriteLineAsync(
                $"Downloading {toolName} {version} for {platform}...").ConfigureAwait(false);
            await DownloadVerifiedFileAsync(
                source,
                archivePath,
                expectedSha256,
                cancellationToken).ConfigureAwait(false);
            await ExtractArchiveAsync(
                archivePath,
                extractionPath,
                cancellationToken).ConfigureAwait(false);

            string sourceExecutablePath = Directory
                .EnumerateFiles(extractionPath, executableName, SearchOption.AllDirectories)
                .Single(path => string.Equals(
                    Path.GetFileName(path),
                    executableName,
                    StringComparison.Ordinal));
            string sourceInstallationPath = sourceExecutablePath;
            for (int level = 0; level <= installationRootLevels; level++)
            {
                sourceInstallationPath = Path.GetDirectoryName(sourceInstallationPath)
                    ?? throw new InvalidDataException(
                        $"The {toolName} archive has no installation root.");
            }

            string executableRelativePath = Path.GetRelativePath(
                sourceInstallationPath,
                sourceExecutablePath);
            Directory.CreateDirectory(Path.GetDirectoryName(installationPath)!);
            if (Directory.Exists(installationPath))
            {
                Directory.Delete(installationPath, recursive: true);
            }

            Directory.Move(sourceInstallationPath, installationPath);
            executablePath = Path.Join(installationPath, executableRelativePath);
            EnsureExecutable(executablePath);
            await VerifyToolAsync(
                executablePath,
                versionArguments,
                expectedVersionText,
                cancellationToken).ConfigureAwait(false);
            return executablePath;
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// Provisions a hash-verified .NET tool package from an isolated local feed.
    /// </summary>
    /// <param name="toolsRoot">The repository tool artifact root.</param>
    /// <param name="toolName">The stable tool directory name.</param>
    /// <param name="packageId">The exact NuGet tool package identifier.</param>
    /// <param name="version">The pinned tool version.</param>
    /// <param name="platform">The stable target platform name.</param>
    /// <param name="packageSource">The official NuGet package URI.</param>
    /// <param name="expectedSha256">The expected NuGet package SHA-256 digest.</param>
    /// <param name="commandName">The installed .NET tool command name.</param>
    /// <param name="versionArguments">Arguments used to query the tool version.</param>
    /// <param name="expectedVersionText">Text required in version output.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The absolute provisioned executable path.</returns>
    internal static async Task<string> ProvisionDotNetToolAsync(
        string toolsRoot,
        string toolName,
        string packageId,
        string version,
        string platform,
        Uri packageSource,
        string expectedSha256,
        string commandName,
        IReadOnlyList<string> versionArguments,
        string expectedVersionText,
        CancellationToken cancellationToken)
    {
        string installationPath = Path.Join(toolsRoot, toolName, version, platform);
        string executablePath = Path.Join(
            installationPath,
            OperatingSystem.IsWindows() ? $"{commandName}.exe" : commandName);
        if (File.Exists(executablePath))
        {
            await VerifyToolAsync(
                executablePath,
                versionArguments,
                expectedVersionText,
                cancellationToken).ConfigureAwait(false);
            return executablePath;
        }

        string stagingRoot = Path.Join(
            toolsRoot,
            ".staging",
            $"{toolName}-{version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        try
        {
            string feedPath = Path.Join(stagingRoot, "feed");
            Directory.CreateDirectory(feedPath);
            string packagePath = Path.Join(
                feedPath,
                $"{packageId}.{version}.nupkg");
            await Console.Error.WriteLineAsync(
                $"Downloading {toolName} {version} for {platform}...").ConfigureAwait(false);
            await DownloadVerifiedFileAsync(
                packageSource,
                packagePath,
                expectedSha256,
                cancellationToken).ConfigureAwait(false);

            string configurationPath = Path.Join(stagingRoot, "NuGet.config");
            var configuration = new XDocument(
                new XElement(
                    "configuration",
                    new XElement(
                        "packageSources",
                        new XElement("clear"),
                        new XElement(
                            "add",
                            new XAttribute("key", "verified-local-feed"),
                            new XAttribute("value", feedPath)))));
            await File.WriteAllTextAsync(
                configurationPath,
                configuration.ToString(SaveOptions.DisableFormatting),
                cancellationToken).ConfigureAwait(false);

            if (Directory.Exists(installationPath))
            {
                Directory.Delete(installationPath, recursive: true);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            foreach (string argument in new[]
            {
                "tool",
                "install",
                packageId,
                "--version",
                version,
                "--tool-path",
                installationPath,
                "--configfile",
                configurationPath,
                "--no-cache"
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The .NET tool installer did not start.");
            Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(
                cancellationToken);
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(
                cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string output = await standardOutputTask.ConfigureAwait(false) +
                await standardErrorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException(
                    $"Installing {packageId} {version} failed with exit code " +
                    $"{process.ExitCode}: {output.Trim()}");
            }

            await VerifyToolAsync(
                executablePath,
                versionArguments,
                expectedVersionText,
                cancellationToken).ConfigureAwait(false);
            return executablePath;
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    private static string FindInstalledExecutable(
        string installationPath,
        string executableName)
    {
        if (!Directory.Exists(installationPath))
        {
            return Path.Join(installationPath, executableName);
        }

        return Directory
            .EnumerateFiles(installationPath, executableName, SearchOption.AllDirectories)
            .SingleOrDefault(path => string.Equals(
                Path.GetFileName(path),
                executableName,
                StringComparison.Ordinal))
            ?? Path.Join(installationPath, executableName);
    }
}
