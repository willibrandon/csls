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
        "Installs the verified Microsoft SBOM Tool release for the current platform.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-SbomTool.cs -- [--output <path>]")
        .ConfigureAwait(false);
    return 0;
}

string? explicitOutput = null;
if (args.Length == 2 && string.Equals(args[0], "--output", StringComparison.Ordinal))
{
    explicitOutput = args[1];
}
else if (args.Length != 0)
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-SbomTool.cs -- [--output <path>]")
        .ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string platform = GetPlatform();
    string executableName = OperatingSystem.IsWindows() ? "sbom-tool.exe" : "sbom-tool";
    string assetName = platform switch
    {
        "linux-x64" => "sbom-tool-linux-x64",
        "osx-arm64" => "sbom-tool-osx-arm64",
        "osx-x64" => "sbom-tool-osx-x64",
        "win-x64" => "sbom-tool-win-x64.exe",
        _ => throw new PlatformNotSupportedException(
            $"Microsoft SBOM Tool does not publish {platform}.")
    };
    (string Tag, string AssetName, Uri Source, string Sha256) release =
        await ScriptSupport.ResolveLatestGitHubReleaseAssetAsync(
            "microsoft",
            "sbom-tool",
            name => string.Equals(name, assetName, StringComparison.Ordinal),
            CancellationToken.None).ConfigureAwait(false);
    string version = release.Tag.TrimStart('v');
    string installationRoot = explicitOutput is null
        ? Path.Join(
            ScriptSupport.ResolveToolsRoot(repositoryRoot),
            "sbom-tool",
            version,
            platform)
        : Path.GetFullPath(explicitOutput);
    string executablePath = Path.Join(installationRoot, executableName);
    if (!File.Exists(executablePath))
    {
        Directory.CreateDirectory(installationRoot);
        string temporaryPath = Path.Join(
            installationRoot,
            $".{executableName}.{Guid.NewGuid():N}.download");
        try
        {
            await ScriptSupport.DownloadVerifiedFileAsync(
                release.Source,
                temporaryPath,
                release.Sha256,
                CancellationToken.None).ConfigureAwait(false);
            File.Move(temporaryPath, executablePath);
            ScriptSupport.EnsureExecutable(executablePath);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    await ScriptSupport.VerifyToolAsync(
        executablePath,
        ["--version"],
        version,
        CancellationToken.None).ConfigureAwait(false);
    string? githubOutput = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
    if (!string.IsNullOrWhiteSpace(githubOutput))
    {
        await File.AppendAllLinesAsync(
            githubOutput,
            [$"path={executablePath}"]).ConfigureAwait(false);
    }

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
    string architecture = RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException(
            $"Microsoft SBOM Tool does not support {RuntimeInformation.OSArchitecture}.")
    };
    if (OperatingSystem.IsWindows())
    {
        return $"win-{architecture}";
    }

    if (OperatingSystem.IsMacOS())
    {
        return $"osx-{architecture}";
    }

    if (OperatingSystem.IsLinux())
    {
        return $"linux-{architecture}";
    }

    throw new PlatformNotSupportedException(
        $"Microsoft SBOM Tool does not support {RuntimeInformation.OSDescription}.");
}
