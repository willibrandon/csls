#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Diagnostics;
using System.Runtime.InteropServices;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Builds, executes, and exports both release containers on the current architecture.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Verify-ReleaseContainers.cs -- " +
        "--version <version> --revision <commit> --output <path>")
        .ConfigureAwait(false);
    return 0;
}

string? version = null;
string? revision = null;
string? outputPath = null;
for (int argumentIndex = 0; argumentIndex < args.Length; argumentIndex += 2)
{
    if (argumentIndex + 1 >= args.Length)
    {
        return await WriteUsageErrorAsync().ConfigureAwait(false);
    }

    string value = args[argumentIndex + 1];
    switch (args[argumentIndex])
    {
        case "--version":
            version = value;
            break;
        case "--revision":
            revision = value;
            break;
        case "--output":
            outputPath = Path.GetFullPath(value);
            break;
        default:
            return await WriteUsageErrorAsync().ConfigureAwait(false);
    }
}

if (version is null || revision is null || outputPath is null)
{
    return await WriteUsageErrorAsync().ConfigureAwait(false);
}

try
{
    if (!OperatingSystem.IsLinux())
    {
        throw new PlatformNotSupportedException(
            "Release container verification requires a Linux Docker host.");
    }

    string architecture = RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException(
            $"Release containers do not support {RuntimeInformation.OSArchitecture}.")
    };
    string repositoryRoot = FindRepositoryRoot();
    string artifactsRoot = Path.GetFullPath(Path.Join(repositoryRoot, "artifacts"));
    string containerOutput = RequirePathInsideArtifacts(artifactsRoot, outputPath);
    Directory.CreateDirectory(containerOutput);
    foreach ((string target, string commandName) in new[]
    {
        ("csls", "csls"),
        ("csls-mcp", "csls-mcp")
    })
    {
        string image = $"csls-release-validation-{target}:{architecture}";
        await RunCheckedAsync(
            "docker",
            [
                "build",
                "--file",
                "deploy/Containerfile",
                "--target",
                target,
                "--build-arg",
                $"VERSION={version}",
                "--build-arg",
                $"REVISION={revision}",
                "--tag",
                image,
                "artifacts/release-final/container"
            ],
            repositoryRoot,
            expectedText: null).ConfigureAwait(false);
        await RunCheckedAsync(
            "docker",
            ["run", "--rm", "--network", "none", image, "--version"],
            repositoryRoot,
            version).ConfigureAwait(false);
        await RunCheckedAsync(
            "dotnet",
            [
                "run",
                "--file",
                "scripts/Export-ContainerImage.cs",
                "--",
                "--image",
                image,
                "--output",
                Path.Join(containerOutput, $"{commandName}-{architecture}.tar")
            ],
            repositoryRoot,
            expectedText: null).ConfigureAwait(false);
    }

    await Console.Out.WriteLineAsync(
        $"Verified and exported both {architecture} release containers.")
        .ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is
    IOException or
    InvalidDataException or
    InvalidOperationException or
    PlatformNotSupportedException or
    UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static async Task<int> WriteUsageErrorAsync()
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Verify-ReleaseContainers.cs -- " +
        "--version <version> --revision <commit> --output <path>")
        .ConfigureAwait(false);
    return 2;
}

static string FindRepositoryRoot()
{
    DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("The csls repository root was not found.");
}

static string RequirePathInsideArtifacts(string artifactsRoot, string path)
{
    string fullPath = Path.GetFullPath(path);
    string prefix = Path.TrimEndingDirectorySeparator(artifactsRoot) +
        Path.DirectorySeparatorChar;
    if (!fullPath.StartsWith(prefix, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"The container output must be inside the repository artifacts directory: {fullPath}");
    }

    return fullPath;
}

static async Task RunCheckedAsync(
    string executablePath,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    string? expectedText)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = executablePath,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        WorkingDirectory = workingDirectory
    };
    foreach (string argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"{executablePath} did not start.");
    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string output = await outputTask.ConfigureAwait(false);
    string error = await errorTask.ConfigureAwait(false);
    await Console.Out.WriteAsync(output).ConfigureAwait(false);
    await Console.Error.WriteAsync(error).ConfigureAwait(false);
    if (process.ExitCode != 0 ||
        (expectedText is not null &&
         !output.Contains(expectedText, StringComparison.OrdinalIgnoreCase)))
    {
        throw new InvalidOperationException(
            $"{executablePath} verification failed with exit code {process.ExitCode}.");
    }
}
