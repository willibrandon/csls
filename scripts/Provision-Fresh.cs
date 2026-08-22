#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Runtime.InteropServices;

const string Version = "0.4.10";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Downloads and verifies the pinned Fresh terminal editor release.").ConfigureAwait(false);
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
    string toolsRoot = args.Length == 2
        ? Path.GetFullPath(args[1])
        : Path.Combine(repositoryRoot, "artifacts", "tools");
    (string platform, string assetName, string expectedSha256, string executableName) =
        SelectAsset();
    var source = new Uri(
        $"https://github.com/sinelaw/fresh/releases/download/v{Version}/{assetName}");
    string executablePath = await ScriptSupport.ProvisionArchiveToolAsync(
        toolsRoot,
        "fresh",
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
    bool musl = File.Exists("/etc/alpine-release");
    if (OperatingSystem.IsLinux() && architecture == Architecture.X64 && !musl)
    {
        return (
            "linux-x64",
            "fresh-editor-x86_64-unknown-linux-gnu.tar.xz",
            "4234d3d35b03f406dd853fa88058aa60aaa5555e27af5e33607076e60d763cf3",
            "fresh");
    }

    if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64 && !musl)
    {
        return (
            "linux-arm64",
            "fresh-editor-aarch64-unknown-linux-gnu.tar.xz",
            "0b51c2b30df8d40c5d2a7730a6e7d8b273cedb379a79cd344ab5fa3dd7d6b256",
            "fresh");
    }

    if (OperatingSystem.IsLinux() && architecture == Architecture.X64 && musl)
    {
        return (
            "linux-musl-x64",
            "fresh-editor-x86_64-unknown-linux-musl.tar.xz",
            "a5b65d9866d04e23ba715253078a0e55ac2f5e650af7f236c72933a40d9f94a3",
            "fresh");
    }

    if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64 && musl)
    {
        return (
            "linux-musl-arm64",
            "fresh-editor-aarch64-unknown-linux-musl.tar.xz",
            "ee1143775f9d3d13d0f807acdb88d07d6f731386603347730b920ed039909b73",
            "fresh");
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.X64)
    {
        return (
            "osx-x64",
            "fresh-editor-x86_64-apple-darwin.tar.xz",
            "b767bc56c4652ffd40202d38b28ae536309eb5f86ecf16794667809a571ab806",
            "fresh");
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.Arm64)
    {
        return (
            "osx-arm64",
            "fresh-editor-aarch64-apple-darwin.tar.xz",
            "cbcebcbcc1caff5c8ef8f545966ad521af19f9069ee7141f2b8561a3ac9ef53a",
            "fresh");
    }

    if (OperatingSystem.IsWindows() && architecture == Architecture.X64)
    {
        return (
            "win-x64",
            "fresh-editor-x86_64-pc-windows-msvc.zip",
            "30abbfce00901b92ce55ef364af1f2d4fdf25e36f36449d3c7cb7d35573f3a8b",
            "fresh.exe");
    }

    if (OperatingSystem.IsWindows() && architecture == Architecture.Arm64)
    {
        return (
            "win-arm64",
            "fresh-editor-aarch64-pc-windows-msvc.zip",
            "8f22e30b7cd831edfde97e23183d9d59a8696786f230656f41bcf8b4cee9473e",
            "fresh.exe");
    }

    throw new PlatformNotSupportedException(
        $"Fresh {Version} has no release asset for {RuntimeInformation.OSDescription} {architecture}.");
}
