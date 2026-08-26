#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Diagnostics;
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
    const string version = "4.1.5";
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string platform = GetPlatform();
    string executableName = OperatingSystem.IsWindows() ? "sbom-tool.exe" : "sbom-tool";
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
        string assetName = platform switch
        {
            "linux-x64" => "sbom-tool-linux-x64",
            "osx-arm64" => "sbom-tool-osx-arm64",
            "osx-x64" => "sbom-tool-osx-x64",
            "win-x64" => "sbom-tool-win-x64.exe",
            _ => throw new PlatformNotSupportedException(
                $"Microsoft SBOM Tool {version} does not publish {platform}.")
        };
        string expectedSha256 = platform switch
        {
            "linux-x64" => "bf5d4f99bc98c119d549d08fc02ae92598a7a42772f17317c01031a92632e05b",
            "osx-arm64" => "bb25842fd707fbe78d3ac9de0d2b27ee2f4a97764f3b8a5c2068c826e75f3535",
            "osx-x64" => "e9a45e3ffdcab920c7bbd2987ce0a133f275241e080bb48c1a3dbe6b558e8ee6",
            "win-x64" => "625767b371b7fdd58f40f618b8a86da0247a33c89e419039c86b4edba1dad4b5",
            _ => throw new UnreachableException()
        };
        string temporaryPath = Path.Join(
            installationRoot,
            $".{executableName}.{Guid.NewGuid():N}.download");
        try
        {
            await ScriptSupport.DownloadVerifiedFileAsync(
                new Uri(
                    $"https://github.com/microsoft/sbom-tool/releases/download/" +
                    $"v{version}/{assetName}"),
                temporaryPath,
                expectedSha256,
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
