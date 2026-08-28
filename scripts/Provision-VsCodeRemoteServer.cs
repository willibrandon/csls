#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Runtime.InteropServices;

const string Version = "1.135.0";
const string Commit = "08d4889f9ec4a1685d257b9b95de036c8e1ce1e5";
const string LinuxX64Sha256 =
    "1aaa94a24066c8c8458b0c043fe210ab1aebfb07e3817b532c8b9db1058e2187";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Downloads and verifies the VS Code server used by remote extension-host tests.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-VsCodeRemoteServer.cs " +
        "[--output <directory>]").ConfigureAwait(false);
    return 0;
}

if (args.Length is not 0 and not 2 ||
    args.Length == 2 && !string.Equals(args[0], "--output", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-VsCodeRemoteServer.cs " +
        "[--output <directory>]").ConfigureAwait(false);
    return 2;
}

try
{
    if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.X64)
    {
        throw new PlatformNotSupportedException(
            $"VS Code remote extension-host testing supports Linux x64, not " +
            $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}.");
    }

    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string toolsRoot = ScriptSupport.ResolveToolsRoot(
        repositoryRoot,
        args.Length == 2 ? args[1] : null);
    string assetName = $"vscode-server-linux-x64-{Version}.tar.gz";
    string serverExecutablePath = await ScriptSupport.ProvisionArchiveToolAsync(
        toolsRoot,
        "vscode-server",
        Version,
        "linux-x64",
        new Uri(
            $"https://update.code.visualstudio.com/commit:{Commit}/" +
            "server-linux-x64/stable"),
        assetName,
        LinuxX64Sha256,
        "code-server",
        installationRootLevels: 1,
        versionArguments: ["--version"],
        expectedVersionText: Version,
        CancellationToken.None).ConfigureAwait(false);
    string serverRoot = Path.GetDirectoryName(
        Path.GetDirectoryName(serverExecutablePath))
        ?? throw new InvalidDataException("The VS Code server root was not found.");
    await Console.Out.WriteLineAsync(serverRoot).ConfigureAwait(false);
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
