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
        "Downloads and verifies the WASI SDK used to build Zed grammars.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-WasiSdk.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length is not 0 and not 2 ||
    args.Length == 2 && !string.Equals(args[0], "--output", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-WasiSdk.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string toolsRoot = ScriptSupport.ResolveToolsRoot(
        repositoryRoot,
        args.Length == 2 ? args[1] : null);
    (string platform, string assetSuffix) = SelectAsset();
    (string tag, string assetName, Uri source, string expectedSha256) =
        await ScriptSupport.ResolveLatestGitHubReleaseAssetAsync(
            "WebAssembly",
            "wasi-sdk",
            name => name.StartsWith("wasi-sdk-", StringComparison.Ordinal) &&
                name.EndsWith(assetSuffix, StringComparison.Ordinal),
            CancellationToken.None).ConfigureAwait(false);
    const string tagPrefix = "wasi-sdk-";
    if (!tag.StartsWith(tagPrefix, StringComparison.Ordinal))
    {
        throw new InvalidDataException($"Unexpected WASI SDK release tag: {tag}");
    }

    string version = tag[tagPrefix.Length..];
    string executablePath = await ScriptSupport.ProvisionArchiveToolAsync(
        toolsRoot,
        "wasi-sdk",
        version,
        platform,
        source,
        assetName,
        expectedSha256,
        OperatingSystem.IsWindows() ? "clang.exe" : "clang",
        installationRootLevels: 1,
        versionArguments: ["--version"],
        expectedVersionText: "wasi-sdk",
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

static (string Platform, string AssetSuffix) SelectAsset()
{
    Architecture architecture = RuntimeInformation.OSArchitecture;
    return (OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), OperatingSystem.IsWindows(),
        architecture) switch
    {
        (true, false, false, Architecture.X64) =>
            ("linux-x64", "-x86_64-linux.tar.gz"),
        (true, false, false, Architecture.Arm64) =>
            ("linux-arm64", "-arm64-linux.tar.gz"),
        (false, true, false, Architecture.X64) =>
            ("osx-x64", "-x86_64-macos.tar.gz"),
        (false, true, false, Architecture.Arm64) =>
            ("osx-arm64", "-arm64-macos.tar.gz"),
        (false, false, true, Architecture.X64) =>
            ("win-x64", "-x86_64-windows.tar.gz"),
        (false, false, true, Architecture.Arm64) =>
            ("win-arm64", "-arm64-windows.tar.gz"),
        _ => throw new PlatformNotSupportedException(
            "WASI SDK does not support this operating system and architecture.")
    };
}
