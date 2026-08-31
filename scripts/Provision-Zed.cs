#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Runtime.InteropServices;

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
        await VerifyExtensionManifestAsync(manifestPath, cancellationToken)
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
    using HttpResponseMessage response = await client.GetAsync(
        source,
        HttpCompletionOption.ResponseHeadersRead,
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
    string[] segments = location.AbsolutePath.Split(
        '/',
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (segments.Length < 4 ||
        !string.Equals(segments[0], "extensions", StringComparison.Ordinal) ||
        !string.Equals(segments[1], "csharp", StringComparison.Ordinal) ||
        !Version.TryParse(segments[2], out _))
    {
        throw new InvalidDataException(
            $"The Zed extension channel returned an invalid release URI: {location}");
    }

    return (segments[2], source);
}

static async Task VerifyExtensionManifestAsync(
    string manifestPath,
    CancellationToken cancellationToken)
{
    string manifest = await File.ReadAllTextAsync(manifestPath, cancellationToken)
        .ConfigureAwait(false);
    if (!manifest.Contains("id = \"csharp\"", StringComparison.Ordinal) ||
        !manifest.Contains("version = \"", StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            "The Zed C# extension manifest is missing its identity or version.");
    }
}
