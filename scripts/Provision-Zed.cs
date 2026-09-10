#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Downloads the latest stable Zed editor and C# extension oracle.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-Zed.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length is not 0 and not 2 ||
    args.Length == 2 && !string.Equals(args[0], "--output", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-Zed.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string toolsRoot = ScriptSupport.ResolveToolsRoot(
        repositoryRoot,
        args.Length == 2 ? args[1] : null);
    (string platform, string selectedAssetName) = SelectAsset();
    (string tag, string assetName, Uri source, string expectedSha256) =
        await ScriptSupport.ResolveLatestGitHubReleaseAssetAsync(
            "zed-industries",
            "zed",
            name => string.Equals(name, selectedAssetName, StringComparison.Ordinal),
            CancellationToken.None).ConfigureAwait(false);
    string version = tag.TrimStart('v');
    string executablePath = await ScriptSupport.ProvisionArchiveToolAsync(
        toolsRoot,
        "zed",
        version,
        platform,
        source,
        assetName,
        expectedSha256,
        "zed",
        installationRootLevels: 1,
        versionArguments: ["--version"],
        expectedVersionText: $"Zed {version}",
        CancellationToken.None).ConfigureAwait(false);
    string extensionPath = await ProvisionCSharpExtensionAsync(
        toolsRoot,
        CancellationToken.None).ConfigureAwait(false);
    await WriteProvisionedOutputsAsync(
        toolsRoot,
        executablePath,
        Path.Join(toolsRoot, "zed", version, platform),
        extensionPath,
        expectedSha256).ConfigureAwait(false);

    await Console.Out.WriteLineAsync(executablePath).ConfigureAwait(false);
    await Console.Out.WriteLineAsync(extensionPath).ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is
    HttpRequestException or
    IOException or
    InvalidDataException or
    InvalidOperationException or
    PlatformNotSupportedException or
    UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static async Task WriteProvisionedOutputsAsync(
    string toolsRoot,
    string executablePath,
    string installationPath,
    string extensionPath,
    string expectedSha256)
{
    string? outputPath = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
    if (string.IsNullOrEmpty(outputPath))
    {
        return;
    }

    string executableRelativePath = GetOutputRelativePath(toolsRoot, executablePath);
    string installationRelativePath = GetOutputRelativePath(toolsRoot, installationPath);
    string extensionRelativePath = GetOutputRelativePath(toolsRoot, extensionPath);
    string cacheKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{expectedSha256}\n{installationRelativePath}\n{extensionRelativePath}")));
    await File.AppendAllTextAsync(
        outputPath,
        $"zed-path={executableRelativePath}{Environment.NewLine}" +
        $"zed-installation={installationRelativePath}{Environment.NewLine}" +
        $"csharp-extension-path={extensionRelativePath}{Environment.NewLine}" +
        $"cache-key={cacheKey}{Environment.NewLine}").ConfigureAwait(false);
}

static string GetOutputRelativePath(string toolsRoot, string path)
{
    string relativePath = Path.GetRelativePath(toolsRoot, path).Replace('\\', '/');
    if (Path.IsPathRooted(relativePath) ||
        relativePath is "." or ".." ||
        relativePath.StartsWith("../", StringComparison.Ordinal) ||
        relativePath.Contains('\r', StringComparison.Ordinal) ||
        relativePath.Contains('\n', StringComparison.Ordinal) ||
        relativePath.Contains('\'', StringComparison.Ordinal))
    {
        throw new InvalidDataException("The provisioned Zed path is not a safe relative path.");
    }

    return relativePath;
}

static (string Platform, string AssetName) SelectAsset()
{
    Architecture architecture = RuntimeInformation.OSArchitecture;
    if (OperatingSystem.IsLinux() && architecture == Architecture.X64)
    {
        return ("linux-x64", "zed-linux-x86_64.tar.gz");
    }

    if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64)
    {
        return ("linux-arm64", "zed-linux-aarch64.tar.gz");
    }

    throw new PlatformNotSupportedException(
        "Zed integration testing supports Linux x64 and arm64.");
}

static async Task<string> ProvisionCSharpExtensionAsync(
    string toolsRoot,
    CancellationToken cancellationToken)
{
    (string version, Uri source) = await ResolveLatestCSharpExtensionAsync(
        cancellationToken).ConfigureAwait(false);
    string installationPath = Path.Join(
        toolsRoot,
        "zed-csharp-extension",
        version,
        "all");
    string manifestPath = Path.Join(installationPath, "extension.toml");
    if (File.Exists(manifestPath))
    {
        await VerifyExtensionManifestAsync(manifestPath, version, cancellationToken)
            .ConfigureAwait(false);
        return installationPath;
    }

    string stagingPath = Path.Join(
        toolsRoot,
        ".staging",
        $"zed-csharp-extension-{Guid.NewGuid():N}");
    Directory.CreateDirectory(stagingPath);
    try
    {
        string archivePath = Path.Join(stagingPath, "csharp.tar.gz");
        string extractionPath = Path.Join(stagingPath, "extracted");
        Directory.CreateDirectory(extractionPath);
        await ScriptSupport.DownloadFileAsync(
            source,
            archivePath,
            cancellationToken).ConfigureAwait(false);
        await ScriptSupport.ExtractArchiveAsync(
            archivePath,
            extractionPath,
            cancellationToken).ConfigureAwait(false);
        await VerifyExtensionManifestAsync(
            Path.Join(extractionPath, "extension.toml"),
            version,
            cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(installationPath)!);
        if (Directory.Exists(installationPath))
        {
            Directory.Delete(installationPath, recursive: true);
        }

        Directory.Move(extractionPath, installationPath);
        return installationPath;
    }
    finally
    {
        if (Directory.Exists(stagingPath))
        {
            Directory.Delete(stagingPath, recursive: true);
        }
    }
}

static async Task<(string Version, Uri Source)> ResolveLatestCSharpExtensionAsync(
    CancellationToken cancellationToken)
{
    var source = new Uri("https://api.zed.dev/extensions/csharp/download");
    using var handler = new HttpClientHandler
    {
        AllowAutoRedirect = false,
        CheckCertificateRevocationList = !OperatingSystem.IsMacOS()
    };
    using var client = new HttpClient(handler)
    {
        Timeout = TimeSpan.FromMinutes(2)
    };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("csls-tool-provisioner");
    using HttpResponseMessage response = await GetExtensionChannelResponseAsync(
        client,
        source,
        cancellationToken).ConfigureAwait(false);
    if (response.StatusCode is not System.Net.HttpStatusCode.TemporaryRedirect &&
        response.StatusCode is not System.Net.HttpStatusCode.Found)
    {
        response.EnsureSuccessStatusCode();
        throw new InvalidDataException(
            "The Zed extension channel did not identify its current release.");
    }

    Uri location = response.Headers.Location
        ?? throw new InvalidDataException(
            "The Zed extension channel returned no current release URI.");
    if (!location.IsAbsoluteUri ||
        !string.Equals(location.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
        !string.IsNullOrEmpty(location.UserInfo))
    {
        throw new InvalidDataException(
            "The Zed extension channel returned an invalid HTTPS release URI.");
    }

    string[] segments = location.AbsolutePath.Split(
        '/',
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (segments.Length != 4 ||
        !string.Equals(segments[0], "extensions", StringComparison.Ordinal) ||
        !string.Equals(segments[1], "csharp", StringComparison.Ordinal) ||
        !Version.TryParse(segments[2], out _) ||
        !string.Equals(segments[3], "archive.tar.gz", StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            "The Zed extension channel returned an invalid release URI.");
    }

    return (segments[2], location);
}

static async Task<HttpResponseMessage> GetExtensionChannelResponseAsync(
    HttpClient client,
    Uri source,
    CancellationToken cancellationToken)
{
    try
    {
        return await client.GetAsync(
            source,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
    {
        throw new HttpRequestException(
            $"The Zed C# extension channel at {source} did not return response headers " +
            "within the configured HTTP timeout.",
            exception);
    }
}

static async Task VerifyExtensionManifestAsync(
    string manifestPath,
    string expectedVersion,
    CancellationToken cancellationToken)
{
    string manifest = await File.ReadAllTextAsync(manifestPath, cancellationToken)
        .ConfigureAwait(false);
    string[] rootEntries =
    [
        .. manifest.Split('\n')
            .Select(static line => line.Trim())
            .TakeWhile(static line => !line.StartsWith('['))
    ];
    if (!rootEntries.Contains("id = \"csharp\"", StringComparer.Ordinal) ||
        !rootEntries.Contains($"version = \"{expectedVersion}\"", StringComparer.Ordinal))
    {
        throw new InvalidDataException(
            $"The Zed C# extension manifest does not identify the selected version {expectedVersion}.");
    }
}
