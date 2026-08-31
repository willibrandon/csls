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
    const string channel = "stable";
    (string revision, string productVersion, Uri source, string sha256) =
        await ScriptSupport.ResolveLatestVsCodeReleaseAsync(
            "server-linux-x64",
            channel,
            CancellationToken.None).ConfigureAwait(false);
    string assetName = $"vscode-server-linux-x64-{productVersion}.tar.gz";
    string serverExecutablePath = await ScriptSupport.ProvisionArchiveToolAsync(
        toolsRoot,
        "vscode-server",
        revision,
        "linux-x64",
        source,
        assetName,
        sha256,
        "code-server",
        installationRootLevels: 1,
        versionArguments: ["--version"],
        expectedVersionText: revision,
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
