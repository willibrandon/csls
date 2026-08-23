#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Diagnostics;
using System.Runtime.CompilerServices;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Exports a local container image to a Picket-scannable Docker archive.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Export-ContainerImage.cs -- " +
        "--image <reference> --output <archive>").ConfigureAwait(false);
    return 0;
}

if (args.Length != 4 ||
    !string.Equals(args[0], "--image", StringComparison.Ordinal) ||
    !string.Equals(args[2], "--output", StringComparison.Ordinal) ||
    string.IsNullOrWhiteSpace(args[1]) ||
    string.IsNullOrWhiteSpace(args[3]))
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Export-ContainerImage.cs -- " +
        "--image <reference> --output <archive>").ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = Path.GetFullPath(Path.Join(GetScriptDirectory(), ".."));
    string artifactsRoot = Path.GetFullPath(Path.Join(repositoryRoot, "artifacts")) +
        Path.DirectorySeparatorChar;
    string outputPath = Path.GetFullPath(args[3]);
    if (!outputPath.StartsWith(artifactsRoot, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Container archives must be written beneath {artifactsRoot}");
    }

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    if (File.Exists(outputPath))
    {
        File.Delete(outputPath);
    }

    await RunDockerAsync(["image", "inspect", args[1]], repositoryRoot).ConfigureAwait(false);
    await RunDockerAsync(
        ["save", "--output", outputPath, args[1]],
        repositoryRoot).ConfigureAwait(false);
    if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
    {
        throw new InvalidDataException(
            $"Docker did not create a nonempty archive at {outputPath}");
    }

    await Console.Out.WriteLineAsync(outputPath).ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is
    IOException or
    InvalidDataException or
    InvalidOperationException or
    UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static string GetScriptDirectory([CallerFilePath] string scriptPath = "") =>
    Path.GetDirectoryName(scriptPath)
    ?? throw new InvalidOperationException("The script directory could not be determined.");

static async Task RunDockerAsync(
    IReadOnlyList<string> arguments,
    string workingDirectory)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "docker",
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
        ?? throw new InvalidOperationException("Docker did not start.");
    Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string standardOutput = await standardOutputTask.ConfigureAwait(false);
    string standardError = await standardErrorTask.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Docker failed with exit code {process.ExitCode}:" +
            $"{Environment.NewLine}{standardOutput}{standardError}");
    }
}
