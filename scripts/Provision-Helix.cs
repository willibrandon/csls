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
        "Downloads and verifies the latest Helix editor release.").ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-Helix.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length is not 0 and not 2 ||
    args.Length == 2 && !string.Equals(args[0], "--output", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-Helix.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string toolsRoot = ScriptSupport.ResolveToolsRoot(
        repositoryRoot,
        args.Length == 2 ? args[1] : null);
    (string platform, string assetSuffix, string executableName) =
        SelectAsset();
    (string tag, string assetName, Uri source, string expectedSha256) =
        await ScriptSupport.ResolveLatestGitHubReleaseAssetAsync(
            "helix-editor",
            "helix",
            name => name.StartsWith("helix-", StringComparison.Ordinal) &&
                name.EndsWith(assetSuffix, StringComparison.Ordinal),
            CancellationToken.None).ConfigureAwait(false);
    string version = tag.TrimStart('v');
    string executablePath = await ScriptSupport.ProvisionArchiveToolAsync(
        toolsRoot,
        "helix",
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

static (string Platform, string AssetSuffix, string ExecutableName) SelectAsset()
{
    Architecture architecture = RuntimeInformation.OSArchitecture;
    if (OperatingSystem.IsLinux() && architecture == Architecture.X64)
    {
        return (
            "linux-x64",
            "-x86_64-linux.tar.xz",
            "hx");
    }

    if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64)
    {
        return (
            "linux-arm64",
            "-aarch64-linux.tar.xz",
            "hx");
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.X64)
    {
        return (
            "osx-x64",
            "-x86_64-macos.tar.xz",
            "hx");
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.Arm64)
    {
        return (
            "osx-arm64",
            "-aarch64-macos.tar.xz",
            "hx");
    }

    if (OperatingSystem.IsWindows() && architecture is Architecture.X64 or Architecture.Arm64)
    {
        return (
            "win-x64",
            "-x86_64-windows.zip",
            "hx.exe");
    }

    throw new PlatformNotSupportedException(
        $"Helix has no release asset for {RuntimeInformation.OSDescription} {architecture}.");
}
