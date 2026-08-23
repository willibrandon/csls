#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Runtime.InteropServices;

const string Version = "0.12.4";

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
            "012bf3fcac5ade43914df3f174668bf64d05e049a4f032a388c027b1ebd78628",
            "nvim");
    }

    if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64)
    {
        return (
            "linux-arm64",
            "nvim-linux-arm64.tar.gz",
            "ceb7e88c6b681f0515d135dcdfad54f5eb4373b25ce6172197cd9a69c758063f",
            "nvim");
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.X64)
    {
        return (
            "osx-x64",
            "nvim-macos-x86_64.tar.gz",
            "03fe16f8dd9f1e9eaf52d5e294913a39917b9e2faea30d7fb0fb385fbd36fe59",
            "nvim");
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.Arm64)
    {
        return (
            "osx-arm64",
            "nvim-macos-arm64.tar.gz",
            "51ab83afa66d663627c2ab1be43209b0f4e81360d4598b53efaa4d8195f24c89",
            "nvim");
    }

    if (OperatingSystem.IsWindows() && architecture == Architecture.X64)
    {
        return (
            "win-x64",
            "nvim-win64.zip",
            "9fc3572829ffd13debb6e32555da2c8cc02555568260a9fc4cf1f65bbcca319c",
            "nvim.exe");
    }

    if (OperatingSystem.IsWindows() && architecture == Architecture.Arm64)
    {
        return (
            "win-arm64",
            "nvim-win-arm64.zip",
            "49906085a3c473ee87a28319942c62216fb365a1a1a4f83dbc4ac41365f5e609",
            "nvim.exe");
    }

    throw new PlatformNotSupportedException(
        $"Neovim {Version} has no release asset for {RuntimeInformation.OSDescription} {architecture}.");
}
