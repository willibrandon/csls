#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Runtime.InteropServices;

const string Version = "0.27.0";
const string PackageSha256 =
    "a49a34d4664e4fba4781cb50d537efad1771393828370e32ece3fe75e2bcedaf";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Downloads and verifies the pinned upstream csharp-ls parity oracle.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-CsharpLsOracle.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length is not 0 and not 2 ||
    args.Length == 2 && !string.Equals(args[0], "--output", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-CsharpLsOracle.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string toolsRoot = ScriptSupport.ResolveToolsRoot(
        repositoryRoot,
        args.Length == 2 ? args[1] : null);
    string platform = GetPlatform();
    var packageSource = new Uri(
        $"https://api.nuget.org/v3-flatcontainer/csharp-ls/{Version}/csharp-ls.{Version}.nupkg");
    string executablePath = await ScriptSupport.ProvisionDotNetToolAsync(
        toolsRoot,
        "csharp-ls-oracle",
        "csharp-ls",
        Version,
        platform,
        packageSource,
        PackageSha256,
        "csharp-ls",
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

static string GetPlatform()
{
    string operatingSystem = OperatingSystem.IsLinux()
        ? "linux"
        : OperatingSystem.IsMacOS()
            ? "osx"
            : OperatingSystem.IsWindows()
                ? "win"
                : throw new PlatformNotSupportedException(
                    $"The upstream oracle does not support {RuntimeInformation.OSDescription}.");
    string architecture = RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException(
            $"The upstream oracle does not support {RuntimeInformation.OSArchitecture}.")
    };
    return $"{operatingSystem}-{architecture}";
}
