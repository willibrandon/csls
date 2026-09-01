using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    /// Resolves one asset from the latest stable GitHub release and returns its published digest.
    /// </summary>
    /// <param name="owner">The GitHub repository owner.</param>
    /// <param name="repository">The GitHub repository name.</param>
    /// <param name="assetSelector">A predicate that selects the platform release asset.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The release tag, asset name, download URI, and SHA-256 digest.</returns>
    internal static async Task<(
        string Tag,
        string AssetName,
        Uri Source,
        string Sha256)> ResolveLatestGitHubReleaseAssetAsync(
        string owner,
        string repository,
        Func<string, bool> assetSelector,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CheckCertificateRevocationList = !OperatingSystem.IsMacOS()
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("csls-tool-provisioner");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                token);
        }

        Uri releaseUri = new(
            $"https://api.github.com/repos/{owner}/{repository}/releases/latest");
        return await ExecuteHttpWithRetriesAsync(
            $"resolve the latest {owner}/{repository} release",
            async () =>
            {
                using HttpResponseMessage response = await client.GetAsync(
                    releaseUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using Stream content = await response.Content
                    .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument document = await JsonDocument.ParseAsync(
                    content,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                JsonElement release = document.RootElement;
                string tag = release.GetProperty("tag_name").GetString()
                    ?? throw new InvalidDataException(
                        $"The latest {owner}/{repository} release has no tag.");
                JsonElement[] assets =
                [
                    .. release
                        .GetProperty("assets")
                        .EnumerateArray()
                        .Where(asset => assetSelector(
                            asset.GetProperty("name").GetString() ?? string.Empty))
                ];
                if (assets.Length != 1)
                {
                    throw new InvalidDataException(
                        $"The latest {owner}/{repository} release {tag} has " +
                        $"{assets.Length} matching assets; exactly one is required.");
                }

                JsonElement selectedAsset = assets[0];
                string assetName = selectedAsset.GetProperty("name").GetString()
                    ?? throw new InvalidDataException(
                        $"The selected {owner}/{repository} release asset has no name.");
                string sourceText = selectedAsset.GetProperty("browser_download_url").GetString()
                    ?? throw new InvalidDataException(
                        $"The {owner}/{repository} release asset {assetName} has no download URI.");
                string digest = selectedAsset.GetProperty("digest").GetString()
                    ?? throw new InvalidDataException(
                        $"The {owner}/{repository} release asset {assetName} has no digest.");
                const string sha256Prefix = "sha256:";
                if (!digest.StartsWith(sha256Prefix, StringComparison.OrdinalIgnoreCase) ||
                    digest.Length != sha256Prefix.Length + 64)
                {
                    throw new InvalidDataException(
                        $"The {owner}/{repository} release asset {assetName} has an invalid digest.");
                }

                return (
                    tag,
                    assetName,
                    new Uri(sourceText),
                    digest[sha256Prefix.Length..]);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the latest stable Visual Studio Marketplace extension package.
    /// </summary>
    /// <param name="publisher">The Marketplace publisher identifier.</param>
    /// <param name="extensionName">The Marketplace extension identifier.</param>
    /// <param name="targetPlatform">The optional VS Code target platform.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The current extension version and VSIX download URI.</returns>
    internal static async Task<(string Version, Uri Source)>
        ResolveLatestVsCodeExtensionAsync(
            string publisher,
            string extensionName,
            string? targetPlatform,
            CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CheckCertificateRevocationList = !OperatingSystem.IsMacOS()
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("csls-tool-provisioner");
        client.DefaultRequestHeaders.Accept.ParseAdd(
            "application/json;api-version=7.2-preview.1");
        return await ExecuteHttpWithRetriesAsync(
            $"resolve {publisher}.{extensionName} from the Visual Studio Marketplace",
            async () =>
            {
                string query = $$"""
                    {"filters":[{"criteria":[{"filterType":7,"value":"{{publisher}}.{{extensionName}}"}],"pageNumber":1,"pageSize":1,"sortBy":0,"sortOrder":0}],"assetTypes":["Microsoft.VisualStudio.Services.VSIXPackage"],"flags":950}
                    """;
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    "https://marketplace.visualstudio.com/_apis/public/gallery/" +
                    "extensionquery?api-version=7.2-preview.1")
                {
                    Content = new StringContent(query, Encoding.UTF8, "application/json")
                };
                using HttpResponseMessage response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using Stream content = await response.Content
                    .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument document = await JsonDocument.ParseAsync(
                    content,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                JsonElement extension = document.RootElement
                    .GetProperty("results")[0]
                    .GetProperty("extensions")[0];
                JsonElement[] matchingVersions =
                [
                    .. extension
                        .GetProperty("versions")
                        .EnumerateArray()
                        .Where(version =>
                        {
                            bool hasPlatform = version.TryGetProperty(
                                "targetPlatform",
                                out JsonElement platform);
                            return targetPlatform is null
                                ? !hasPlatform || platform.ValueKind == JsonValueKind.Null
                                : hasPlatform && string.Equals(
                                    platform.GetString(),
                                    targetPlatform,
                                    StringComparison.Ordinal);
                        })
                ];
                if (matchingVersions.Length == 0)
                {
                    throw new InvalidDataException(
                        $"The Marketplace returned no {publisher}.{extensionName} package for " +
                        $"{targetPlatform ?? "all platforms"}.");
                }

                JsonElement selectedVersion = matchingVersions[0];
                string versionText = selectedVersion.GetProperty("version").GetString()
                    ?? throw new InvalidDataException(
                        $"The Marketplace returned {publisher}.{extensionName} without a version.");
                JsonElement package = selectedVersion
                    .GetProperty("files")
                    .EnumerateArray()
                    .Single(file => string.Equals(
                        file.GetProperty("assetType").GetString(),
                        "Microsoft.VisualStudio.Services.VSIXPackage",
                        StringComparison.Ordinal));
                string sourceText = package.GetProperty("source").GetString()
                    ?? throw new InvalidDataException(
                        $"The Marketplace returned {publisher}.{extensionName} without a VSIX URI.");
                return (versionText, new Uri(sourceText));
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a current Visual Studio Code release from an official update channel.
    /// </summary>
    /// <param name="target">The Visual Studio Code update target.</param>
    /// <param name="channel">The Visual Studio Code release channel.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The revision, product version, download URI, and publisher SHA-256 digest.</returns>
    internal static async Task<(
        string Revision,
        string ProductVersion,
        Uri Source,
        string Sha256)> ResolveLatestVsCodeReleaseAsync(
        string target,
        string channel,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CheckCertificateRevocationList = !OperatingSystem.IsMacOS()
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("csls-tool-provisioner");
        Uri releaseUri = new(
            $"https://update.code.visualstudio.com/api/update/" +
            $"{Uri.EscapeDataString(target)}/{Uri.EscapeDataString(channel)}/latest");
        return await ExecuteHttpWithRetriesAsync(
            $"resolve the Visual Studio Code {channel} release for {target}",
            async () =>
            {
                using HttpResponseMessage response = await client.GetAsync(
                    releaseUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using Stream content = await response.Content
                    .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument document = await JsonDocument.ParseAsync(
                    content,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                JsonElement release = document.RootElement;
                string revision = release.GetProperty("version").GetString()
                    ?? throw new InvalidDataException(
                        $"The Visual Studio Code {channel} release has no revision.");
                string productVersion = release.GetProperty("productVersion").GetString()
                    ?? throw new InvalidDataException(
                        $"The Visual Studio Code {channel} release has no product version.");
                string sourceText = release.GetProperty("url").GetString()
                    ?? throw new InvalidDataException(
                        $"The Visual Studio Code {channel} release has no download URI.");
                string sha256 = release.GetProperty("sha256hash").GetString()
                    ?? throw new InvalidDataException(
                        $"The Visual Studio Code {channel} release has no SHA-256 digest.");
                if (revision.Length != 40 || !revision.All(Uri.IsHexDigit) ||
                    sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
                {
                    throw new InvalidDataException(
                        $"The Visual Studio Code {channel} release metadata is invalid.");
                }

                return (revision, productVersion, new Uri(sourceText), sha256);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads a file and rejects it unless its SHA-256 digest matches the publisher's digest.
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
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("csls-tool-provisioner");
        string stagingPath = $"{destinationPath}.{Guid.NewGuid():N}.download";
        try
        {
            await ExecuteHttpWithRetriesAsync(
                $"download {source}",
                async () =>
                {
                    File.Delete(stagingPath);
                    using HttpResponseMessage response = await client
                        .GetAsync(
                            source,
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellationToken)
                        .ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    using (FileStream destination = new(
                        stagingPath,
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

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            string actualSha256 = await ComputeSha256Async(
                stagingPath,
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

            File.Move(stagingPath, destinationPath);
        }
        finally
        {
            File.Delete(stagingPath);
        }
    }

    /// <summary>
    /// Downloads a file from an HTTPS release channel when the publisher exposes no digest.
    /// </summary>
    /// <param name="source">The source URI.</param>
    /// <param name="destinationPath">The destination file path.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task that completes after the file is downloaded.</returns>
    internal static async Task DownloadFileAsync(
        Uri source,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Tool downloads require HTTPS: {source}");
        }

        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CheckCertificateRevocationList = !OperatingSystem.IsMacOS()
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("csls-tool-provisioner");
        string stagingPath = $"{destinationPath}.{Guid.NewGuid():N}.download";
        try
        {
            await ExecuteHttpWithRetriesAsync(
                $"download {source}",
                async () =>
                {
                    File.Delete(stagingPath);
                    using HttpResponseMessage response = await client.GetAsync(
                        source,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    using FileStream destination = new(
                        stagingPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 131_072,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await response.Content.CopyToAsync(destination, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            File.Move(stagingPath, destinationPath);
        }
        finally
        {
            File.Delete(stagingPath);
        }
    }

    private static async Task<T> ExecuteHttpWithRetriesAsync<T>(
        string operation,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception exception) when (
                attempt < maximumAttempts &&
                IsTransientHttpFailure(exception, cancellationToken))
            {
                var delay = TimeSpan.FromSeconds(attempt * 2);
                await Console.Error.WriteLineAsync(
                    $"HTTP attempt {attempt} failed while trying to {operation}: " +
                    $"{exception.GetBaseException().Message} Retrying in " +
                    $"{delay.TotalSeconds:F0} seconds.").ConfigureAwait(false);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientHttpFailure(
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        if (exception is IOException)
        {
            return true;
        }

        if (exception is not HttpRequestException requestException)
        {
            return false;
        }

        return requestException.StatusCode is null or
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;
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
        using FileStream input = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 131_072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] digest = await SHA256.HashDataAsync(input, cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexStringLower(digest);
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
    /// <param name="expectedText">Optional text required in combined output.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task that completes after the expected version is observed.</returns>
    internal static async Task VerifyToolAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string? expectedText,
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
        if (process.ExitCode != 0 ||
            expectedText is not null && !output.Contains(expectedText, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Expected successful version output from '{executablePath}', but it returned " +
                $"exit code {process.ExitCode}: {output.Trim()}");
        }
    }

    /// <summary>
    /// Provisions and validates an archive-distributed development tool release.
    /// </summary>
    /// <param name="toolsRoot">The repository tool artifact root.</param>
    /// <param name="toolName">The stable tool directory name.</param>
    /// <param name="version">The resolved tool release version.</param>
    /// <param name="platform">The stable target platform name.</param>
    /// <param name="source">The official release asset URI.</param>
    /// <param name="assetName">The release asset file name.</param>
    /// <param name="expectedSha256">The optional publisher-provided release asset digest.</param>
    /// <param name="executableName">The executable file name in the archive.</param>
    /// <param name="installationRootLevels">Parent levels from the executable to the installation root.</param>
    /// <param name="versionArguments">Arguments used to query the tool version.</param>
    /// <param name="expectedVersionText">Optional text required in version output.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The absolute provisioned executable path.</returns>
    internal static async Task<string> ProvisionArchiveToolAsync(
        string toolsRoot,
        string toolName,
        string version,
        string platform,
        Uri source,
        string assetName,
        string? expectedSha256,
        string executableName,
        int installationRootLevels,
        IReadOnlyList<string> versionArguments,
        string? expectedVersionText,
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
            if (expectedSha256 is null)
            {
                await DownloadFileAsync(
                    source,
                    archivePath,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await DownloadVerifiedFileAsync(
                    source,
                    archivePath,
                    expectedSha256,
                    cancellationToken).ConfigureAwait(false);
            }
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
    /// <param name="packageId">The NuGet tool package identifier.</param>
    /// <param name="version">The resolved tool package version.</param>
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
