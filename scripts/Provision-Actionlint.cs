#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Runtime.InteropServices;

const string Version = "1.7.12";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Downloads and verifies the pinned actionlint GitHub Actions validator.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-Actionlint.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length is not 0 and not 2 ||
    args.Length == 2 && !string.Equals(args[0], "--output", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-Actionlint.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string toolsRoot = ScriptSupport.ResolveToolsRoot(
        repositoryRoot,
        args.Length == 2 ? args[1] : null);
    (string platform, string assetPlatform, string expectedSha256) = SelectAsset();
    string assetExtension = OperatingSystem.IsWindows() ? "zip" : "tar.gz";
    string assetName = $"actionlint_{Version}_{assetPlatform}.{assetExtension}";
    string executableName = OperatingSystem.IsWindows() ? "actionlint.exe" : "actionlint";
    string executablePath = await ScriptSupport.ProvisionArchiveToolAsync(
        toolsRoot,
        "actionlint",
        Version,
        platform,
        new Uri($"https://github.com/rhysd/actionlint/releases/download/v{Version}/{assetName}"),
        assetName,
        expectedSha256,
        executableName,
        installationRootLevels: 0,
        versionArguments: ["-version"],
        expectedVersionText: Version,
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

static (string Platform, string AssetPlatform, string Sha256) SelectAsset()
{
    Architecture architecture = RuntimeInformation.OSArchitecture;
    if (OperatingSystem.IsLinux() && architecture == Architecture.X64)
    {
        return (
            "linux-x64",
            "linux_amd64",
            "8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8");
    }

    if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64)
    {
        return (
            "linux-arm64",
            "linux_arm64",
            "325e971b6ba9bfa504672e29be93c24981eeb1c07576d730e9f7c8805afff0c6");
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.X64)
    {
        return (
            "osx-x64",
            "darwin_amd64",
            "5b44c3bc2255115c9b69e30efc0fecdf498fdb63c5d58e17084fd5f16324c644");
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.Arm64)
    {
        return (
            "osx-arm64",
            "darwin_arm64",
            "aba9ced2dee8d27fecca3dc7feb1a7f9a52caefa1eb46f3271ea66b6e0e6953f");
    }

    if (OperatingSystem.IsWindows() && architecture == Architecture.X64)
    {
        return (
            "win-x64",
            "windows_amd64",
            "6e7241b51e6817ea6a047693d8e6fed13b31819c9a0dd6c5a726e1592d22f6e9");
    }

    if (OperatingSystem.IsWindows() && architecture == Architecture.Arm64)
    {
        return (
            "win-arm64",
            "windows_arm64",
            "cadcf7ea4efe3a68728893813643cebe1185e5b1d4be5b96245f65c9a4d5ea41");
    }

    throw new PlatformNotSupportedException(
        $"actionlint {Version} has no release asset for " +
        $"{RuntimeInformation.OSDescription} {architecture}.");
}
