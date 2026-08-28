#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Runtime.InteropServices;

const string Version = "1.17.2";
const string CSharpExtensionVersion = "1.2.2";
const string CSharpExtensionSha256 =
    "899714536549b3ce4d43b758f74e5376ac68d4814a08376b6bd5be0fbbb23195";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Downloads and verifies the pinned Zed editor and C# extension oracle.")
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
    (string platform, string assetName, string expectedSha256) = SelectAsset();
    var source = new Uri(
        $"https://github.com/zed-industries/zed/releases/download/v{Version}/{assetName}");
    string executablePath = await ScriptSupport.ProvisionArchiveToolAsync(
        toolsRoot,
        "zed",
        Version,
        platform,
        source,
        assetName,
        expectedSha256,
        "zed",
        installationRootLevels: 1,
        versionArguments: ["--version"],
        expectedVersionText: $"Zed {Version}",
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

static (string Platform, string AssetName, string Sha256) SelectAsset()
{
    Architecture architecture = RuntimeInformation.OSArchitecture;
    if (OperatingSystem.IsLinux() && architecture == Architecture.X64)
    {
        return (
            "linux-x64",
            "zed-linux-x86_64.tar.gz",
            "3682dd058a305d2b246a14d64419fcf42e86a06e27755d23b5a28622ed9aef85");
    }

    if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64)
    {
        return (
            "linux-arm64",
            "zed-linux-aarch64.tar.gz",
            "4f75332ab8155a5a62b0cdc473473cf8938959cf3cd2b0145e2975969d7e8929");
    }

    throw new PlatformNotSupportedException(
        $"Zed {Version} integration testing supports Linux x64 and arm64.");
}

static async Task<string> ProvisionCSharpExtensionAsync(
    string toolsRoot,
    CancellationToken cancellationToken)
{
    string installationPath = Path.Join(
        toolsRoot,
        "zed-csharp-extension",
        CSharpExtensionVersion,
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
        $"zed-csharp-extension-{CSharpExtensionVersion}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(stagingPath);
    try
    {
        string archivePath = Path.Join(stagingPath, "csharp.tar.gz");
        string extractionPath = Path.Join(stagingPath, "extracted");
        Directory.CreateDirectory(extractionPath);
        await ScriptSupport.DownloadVerifiedFileAsync(
            new Uri(
                $"https://api.zed.dev/extensions/csharp/{CSharpExtensionVersion}/download"),
            archivePath,
            CSharpExtensionSha256,
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

static async Task VerifyExtensionManifestAsync(
    string manifestPath,
    CancellationToken cancellationToken)
{
    string manifest = await File.ReadAllTextAsync(manifestPath, cancellationToken)
        .ConfigureAwait(false);
    if (!manifest.Contains("id = \"csharp\"", StringComparison.Ordinal) ||
        !manifest.Contains(
            $"version = \"{CSharpExtensionVersion}\"",
            StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"The Zed C# extension manifest is not version {CSharpExtensionVersion}.");
    }
}
