#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Runtime.InteropServices;

const string Version = "34.0";

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
    (string platform, string assetName, string expectedSha256) = SelectAsset();
    var source = new Uri(
        $"https://github.com/WebAssembly/wasi-sdk/releases/download/wasi-sdk-34/{assetName}");
    string executablePath = await ScriptSupport.ProvisionArchiveToolAsync(
        toolsRoot,
        "wasi-sdk",
        Version,
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

static (string Platform, string AssetName, string Sha256) SelectAsset()
{
    Architecture architecture = RuntimeInformation.OSArchitecture;
    return (OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), OperatingSystem.IsWindows(),
        architecture) switch
    {
        (true, false, false, Architecture.X64) =>
            ("linux-x64", "wasi-sdk-34.0-x86_64-linux.tar.gz",
                "b761e3a0721dbae9c09a0059e5fdb2bf917d1b4a8a7b430fb3b5aafb0984b2c4"),
        (true, false, false, Architecture.Arm64) =>
            ("linux-arm64", "wasi-sdk-34.0-arm64-linux.tar.gz",
                "f7e243dff54d60bcc576e94d6166b69f410f2500ae4a9ceef34315be10e77971"),
        (false, true, false, Architecture.X64) =>
            ("osx-x64", "wasi-sdk-34.0-x86_64-macos.tar.gz",
                "87d27fa8adc68dee59bfbf2e22a6d34ef717c34d6bf1d8af2a56fc929d9ce0eb"),
        (false, true, false, Architecture.Arm64) =>
            ("osx-arm64", "wasi-sdk-34.0-arm64-macos.tar.gz",
                "9c59398106b417f8f14913380fdf0097a8cc0ff4af9eb3ce0065a859e88d49e9"),
        (false, false, true, Architecture.X64) =>
            ("win-x64", "wasi-sdk-34.0-x86_64-windows.tar.gz",
                "cccb5c323a9b34f0349a9b09e8804a0a7632c68c3310f4b5f437ed57d7e71d8f"),
        (false, false, true, Architecture.Arm64) =>
            ("win-arm64", "wasi-sdk-34.0-arm64-windows.tar.gz",
                "45e1c71f3e965621e7b98ebe1d37b0e4b1f77f3e8072113ffb4534e67b1a4b7c"),
        _ => throw new PlatformNotSupportedException(
            $"WASI SDK {Version} does not support this operating system and architecture.")
    };
}
