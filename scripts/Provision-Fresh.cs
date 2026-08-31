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
        "Downloads and verifies the latest Fresh terminal editor release.").ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-Fresh.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length is not 0 and not 2 ||
    args.Length == 2 && !string.Equals(args[0], "--output", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-Fresh.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string toolsRoot = ScriptSupport.ResolveToolsRoot(
        repositoryRoot,
        args.Length == 2 ? args[1] : null);
    (string platform, string selectedAssetName, string executableName) =
        SelectAsset();
    (string tag, string assetName, Uri source, string expectedSha256) =
        await ScriptSupport.ResolveLatestGitHubReleaseAssetAsync(
            "sinelaw",
            "fresh",
            name => string.Equals(name, selectedAssetName, StringComparison.Ordinal),
            CancellationToken.None).ConfigureAwait(false);
    string version = tag.TrimStart('v');
    string executablePath = await ScriptSupport.ProvisionArchiveToolAsync(
        toolsRoot,
        "fresh",
        version,
        platform,
        source,
        assetName,
        expectedSha256,
        executableName,
        installationRootLevels: 0,
        versionArguments: ["--version"],
        expectedVersionText: version,
        CancellationToken.None).ConfigureAwait(false);

    await Console.Out.WriteLineAsync(executablePath).ConfigureAwait(false);
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

static (string Platform, string AssetName, string ExecutableName) SelectAsset()
{
    Architecture architecture = RuntimeInformation.OSArchitecture;
    bool musl = File.Exists("/etc/alpine-release");
    if (OperatingSystem.IsLinux() && architecture == Architecture.X64 && !musl)
    {
        return (
            "linux-x64",
            "fresh-editor-x86_64-unknown-linux-gnu.tar.xz",
            "fresh");
    }

    if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64 && !musl)
    {
        return (
            "linux-arm64",
            "fresh-editor-aarch64-unknown-linux-gnu.tar.xz",
            "fresh");
    }

    if (OperatingSystem.IsLinux() && architecture == Architecture.X64 && musl)
    {
        return (
            "linux-musl-x64",
            "fresh-editor-x86_64-unknown-linux-musl.tar.xz",
            "fresh");
    }

    if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64 && musl)
    {
        return (
            "linux-musl-arm64",
            "fresh-editor-aarch64-unknown-linux-musl.tar.xz",
            "fresh");
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.X64)
    {
        return (
            "osx-x64",
            "fresh-editor-x86_64-apple-darwin.tar.xz",
            "fresh");
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.Arm64)
    {
        return (
            "osx-arm64",
            "fresh-editor-aarch64-apple-darwin.tar.xz",
            "fresh");
    }

    if (OperatingSystem.IsWindows() && architecture == Architecture.X64)
    {
        return (
            "win-x64",
            "fresh-editor-x86_64-pc-windows-msvc.zip",
            "fresh.exe");
    }

    if (OperatingSystem.IsWindows() && architecture == Architecture.Arm64)
    {
        return (
            "win-arm64",
            "fresh-editor-aarch64-pc-windows-msvc.zip",
            "fresh.exe");
    }

    throw new PlatformNotSupportedException(
        $"Fresh has no release asset for {RuntimeInformation.OSDescription} {architecture}.");
}
