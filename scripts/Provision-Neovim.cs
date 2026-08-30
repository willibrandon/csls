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
        "Downloads and verifies the latest Neovim editor release.").ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-Neovim.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length is not 0 and not 2 ||
    args.Length == 2 && !string.Equals(args[0], "--output", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-Neovim.cs [--output <directory>]")
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
            "neovim",
            "neovim",
            name => string.Equals(name, selectedAssetName, StringComparison.Ordinal),
            CancellationToken.None).ConfigureAwait(false);
    string version = tag.TrimStart('v');
    string executablePath = await ScriptSupport.ProvisionArchiveToolAsync(
        toolsRoot,
        "neovim",
        version,
        platform,
        source,
        assetName,
        expectedSha256,
        executableName,
        installationRootLevels: 1,
        versionArguments: ["--version"],
        expectedVersionText: $"NVIM v{version}",
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
    if (OperatingSystem.IsLinux() && architecture == Architecture.X64)
    {
        return (
            "linux-x64",
            "nvim-linux-x86_64.tar.gz",
            "nvim");
    }

    if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64)
    {
        return (
            "linux-arm64",
            "nvim-linux-arm64.tar.gz",
            "nvim");
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.X64)
    {
        return (
            "osx-x64",
            "nvim-macos-x86_64.tar.gz",
            "nvim");
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.Arm64)
    {
        return (
            "osx-arm64",
            "nvim-macos-arm64.tar.gz",
            "nvim");
    }

    if (OperatingSystem.IsWindows() && architecture == Architecture.X64)
    {
        return (
            "win-x64",
            "nvim-win64.zip",
            "nvim.exe");
    }

    if (OperatingSystem.IsWindows() && architecture == Architecture.Arm64)
    {
        return (
            "win-arm64",
            "nvim-win-arm64.zip",
            "nvim.exe");
    }

    throw new PlatformNotSupportedException(
        $"Neovim has no release asset for {RuntimeInformation.OSDescription} {architecture}.");
}
