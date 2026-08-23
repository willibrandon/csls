#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Runtime.InteropServices;

const string Version = "25.07.1";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Downloads and verifies the pinned Helix editor release.").ConfigureAwait(false);
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
    (string platform, string assetName, string expectedSha256, string executableName) =
        SelectAsset();
    var source = new Uri(
        $"https://github.com/helix-editor/helix/releases/download/{Version}/{assetName}");
    string executablePath = await ScriptSupport.ProvisionArchiveToolAsync(
        toolsRoot,
        "helix",
        Version,
        platform,
        source,
        assetName,
        expectedSha256,
        executableName,
        installationRootLevels: 0,
        versionArguments: ["--version"],
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

static (string Platform, string AssetName, string Sha256, string ExecutableName) SelectAsset()
{
    Architecture architecture = RuntimeInformation.OSArchitecture;
    if (OperatingSystem.IsLinux() && architecture == Architecture.X64)
    {
        return (
            "linux-x64",
            "helix-25.07.1-x86_64-linux.tar.xz",
            "3f08e63ecd388fff657ad39722f88bb03dcf326f1f2da2700d99e1dc40ab2e8b",
            "hx");
    }

    if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64)
    {
        return (
            "linux-arm64",
            "helix-25.07.1-aarch64-linux.tar.xz",
            "ce23fa8d395e633e3e54c052012f11965d91d8d5c2bfa659685f50430b4f8175",
            "hx");
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.X64)
    {
        return (
            "osx-x64",
            "helix-25.07.1-x86_64-macos.tar.xz",
            "84dc32d617d28d32f4aa21e3aafac47bd715d1154aeb977697d4d60b887b7103",
            "hx");
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.Arm64)
    {
        return (
            "osx-arm64",
            "helix-25.07.1-aarch64-macos.tar.xz",
            "00b1651b4fdbbe0a2ae981c8e76b858bd26a7c33f5b3583f3b6bb9137d54f1ff",
            "hx");
    }

    if (OperatingSystem.IsWindows() && architecture is Architecture.X64 or Architecture.Arm64)
    {
        return (
            "win-x64",
            "helix-25.07.1-x86_64-windows.zip",
            "5c8325ced8bacd8418d62706f669e96d9c3578a9237526e34d546900cbc049b6",
            "hx.exe");
    }

    throw new PlatformNotSupportedException(
        $"Helix {Version} has no release asset for {RuntimeInformation.OSDescription} {architecture}.");
}
