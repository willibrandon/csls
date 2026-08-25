#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Runtime.InteropServices;

const string Version = "0.12.5";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Downloads and verifies the pinned Neovim editor release.").ConfigureAwait(false);
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
    (string platform, string assetName, string expectedSha256, string executableName) =
        SelectAsset();
    var source = new Uri(
        $"https://github.com/neovim/neovim/releases/download/v{Version}/{assetName}");
    string executablePath = await ScriptSupport.ProvisionArchiveToolAsync(
        toolsRoot,
        "neovim",
        Version,
        platform,
        source,
        assetName,
        expectedSha256,
        executableName,
        installationRootLevels: 1,
        versionArguments: ["--version"],
        expectedVersionText: $"NVIM v{Version}",
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

static (string Platform, string AssetName, string Sha256, string ExecutableName) SelectAsset()
{
    Architecture architecture = RuntimeInformation.OSArchitecture;
    if (OperatingSystem.IsLinux() && architecture == Architecture.X64)
    {
        return (
            "linux-x64",
            "nvim-linux-x86_64.tar.gz",
            "bce0f56eda1f1b1db6eee8f4133d7a38813ea07933837dd1777411ca384c6875",
            "nvim");
    }

    if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64)
    {
        return (
            "linux-arm64",
            "nvim-linux-arm64.tar.gz",
            "1aa5ca085249580ae0f91eb14f27ec0919773ff2d99a163d03f3d6c21ac29725",
            "nvim");
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.X64)
    {
        return (
            "osx-x64",
            "nvim-macos-x86_64.tar.gz",
            "81f4518622cb059b450ee2e498c6a1082a222f6bd89589de5bbcf0c6a68aa3fd",
            "nvim");
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.Arm64)
    {
        return (
            "osx-arm64",
            "nvim-macos-arm64.tar.gz",
            "65fb000099e47ca1b762584c484cc833f40e30851a0ec450d4174e16317c1f9b",
            "nvim");
    }

    if (OperatingSystem.IsWindows() && architecture == Architecture.X64)
    {
        return (
            "win-x64",
            "nvim-win64.zip",
            "de8625ba8cf65ebf40eb80a388ba1ec8e9c15b30218821e2c639119b05920de1",
            "nvim.exe");
    }

    if (OperatingSystem.IsWindows() && architecture == Architecture.Arm64)
    {
        return (
            "win-arm64",
            "nvim-win-arm64.zip",
            "f5a2f7ee4603e0185ed5c3e6dc9db762499426baf1c6613a487da5b5e126ae55",
            "nvim.exe");
    }

    throw new PlatformNotSupportedException(
        $"Neovim {Version} has no release asset for {RuntimeInformation.OSDescription} {architecture}.");
}
