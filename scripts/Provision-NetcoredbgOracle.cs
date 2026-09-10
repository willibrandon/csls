#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Runtime.InteropServices;

const string usage = "Usage: dotnet run --file scripts/Provision-NetcoredbgOracle.cs [--github-env]";
if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync("Downloads and verifies the current netcoredbg DAP oracle.").ConfigureAwait(false);
    await Console.Out.WriteLineAsync(usage).ConfigureAwait(false);
    return 0;
}
if (args.Length > 1 || args.Length == 1 && args[0] != "--github-env")
{
    await Console.Error.WriteLineAsync(usage).ConfigureAwait(false);
    return 2;
}

try
{
    string? environmentFile = args.Length == 1 ? Environment.GetEnvironmentVariable("GITHUB_ENV") : null;
    if (args.Length == 1 && string.IsNullOrWhiteSpace(environmentFile))
    {
        throw new InvalidOperationException("--github-env requires the GitHub Actions environment file.");
    }
    (string platform, string asset, string executable) = (OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(),
        OperatingSystem.IsWindows(), RuntimeInformation.OSArchitecture) switch
    {
        (true, _, _, Architecture.X64) => ("linux-x64", "netcoredbg-linux-amd64.tar.gz", "netcoredbg"),
        (true, _, _, Architecture.Arm64) => ("linux-arm64", "netcoredbg-linux-arm64.tar.gz", "netcoredbg"),
        (_, true, _, Architecture.Arm64) => ("osx-arm64", "netcoredbg-osx-arm64.zip", "netcoredbg"),
        (_, _, true, Architecture.X64) => ("win-x64", "netcoredbg-win64.zip", "netcoredbg.exe"),
        _ => throw new PlatformNotSupportedException(
            $"No netcoredbg release asset exists for {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}.")
    };
    (string tag, string assetName, Uri source, string digest) = await ScriptSupport.ResolveLatestGitHubReleaseAssetAsync(
        "Samsung", "netcoredbg", name => name == asset, CancellationToken.None).ConfigureAwait(false);
    string path = await ScriptSupport.ProvisionArchiveToolAsync(
        ScriptSupport.ResolveToolsRoot(ScriptSupport.FindRepositoryRoot()), "netcoredbg-oracle", tag, platform,
        source, assetName, digest, executable, installationRootLevels: 0,
        versionArguments: ["--version"], expectedVersionText: "NET Core debugger", CancellationToken.None).ConfigureAwait(false);
    if (environmentFile is not null)
    {
        if (path.Contains('\r', StringComparison.Ordinal) || path.Contains('\n', StringComparison.Ordinal))
        {
            throw new InvalidDataException("The oracle executable path must fit on one environment-file line.");
        }
        await File.AppendAllTextAsync(environmentFile, $"CSLS_DAP_ORACLE_PATH={path}{Environment.NewLine}")
            .ConfigureAwait(false);
    }
    await Console.Out.WriteLineAsync(path).ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or
    InvalidOperationException or PlatformNotSupportedException or UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}
