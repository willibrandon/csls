#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Diagnostics;

string? job = null;
for (int index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--job" when index + 1 < args.Length:
            job = args[++index];
            break;
        case "--help" or "-h" or "-?":
            await Console.Out.WriteLineAsync(
                "Builds and runs the permanent BenchmarkDotNet suite.").ConfigureAwait(false);
            await Console.Out.WriteLineAsync(
                "Usage: dotnet run --file scripts/Run-Benchmarks.cs -- [--job Dry|Short]")
                .ConfigureAwait(false);
            return 0;
        default:
            await Console.Error.WriteLineAsync($"Unknown argument: {args[index]}")
                .ConfigureAwait(false);
            return 2;
    }
}

if (job is not null && job is not ("Dry" or "Short"))
{
    await Console.Error.WriteLineAsync("--job must be Dry or Short.").ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string dotnetPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    string benchmarkProject = Path.Join(
        repositoryRoot,
        "benchmarks",
        "Csls.Benchmarks",
        "Csls.Benchmarks.csproj");
    string artifactDirectory = Path.Join(repositoryRoot, "artifacts", "benchmarks");
    Directory.CreateDirectory(artifactDirectory);

    await RunCheckedAsync(
        dotnetPath,
        ["build", benchmarkProject, "--configuration", "Release"],
        repositoryRoot).ConfigureAwait(false);

    var benchmarkArguments = new List<string>
    {
        "run",
        "--project",
        benchmarkProject,
        "--configuration",
        "Release",
        "--no-build",
        "--",
        "--filter",
        "*",
        "--artifacts",
        artifactDirectory,
        "--noOverwrite",
        "--exporters",
        "fulljson"
    };
    if (job is not null)
    {
        benchmarkArguments.Add("--job");
        benchmarkArguments.Add(job);
    }

    var existingResultDirectories = Directory
        .EnumerateDirectories(artifactDirectory)
        .ToHashSet(StringComparer.Ordinal);
    string logPath = Path.Join(artifactDirectory, "benchmark.log");
    await RunBenchmarksAsync(
        dotnetPath,
        benchmarkArguments,
        repositoryRoot,
        logPath).ConfigureAwait(false);

    string? resultDirectory = Directory
        .EnumerateDirectories(artifactDirectory)
        .Where(directory => !existingResultDirectories.Contains(directory))
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault();
    if (resultDirectory is not null)
    {
        foreach (string reportPath in Directory
            .EnumerateFiles(resultDirectory, "*-report-github.md")
            .Order(StringComparer.Ordinal))
        {
            await Console.Out.WriteLineAsync(
                await File.ReadAllTextAsync(reportPath).ConfigureAwait(false)).ConfigureAwait(false);
        }
    }

    await Console.Out.WriteLineAsync(
        $"Benchmark log: {Path.GetRelativePath(repositoryRoot, logPath)}").ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is
    IOException or
    InvalidOperationException or
    UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static async Task RunCheckedAsync(
    string executablePath,
    IReadOnlyList<string> arguments,
    string workingDirectory)
{
    (int exitCode, string standardOutput, string standardError) = await RunProcessAsync(
        executablePath,
        arguments,
        workingDirectory).ConfigureAwait(false);
    await Console.Out.WriteAsync(standardOutput).ConfigureAwait(false);
    await Console.Error.WriteAsync(standardError).ConfigureAwait(false);
    if (exitCode != 0)
    {
        throw new InvalidOperationException(
            $"{executablePath} failed with exit code {exitCode}.");
    }
}

static async Task RunBenchmarksAsync(
    string executablePath,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    string logPath)
{
    (int exitCode, string standardOutput, string standardError) = await RunProcessAsync(
        executablePath,
        arguments,
        workingDirectory).ConfigureAwait(false);
    await File.WriteAllTextAsync(
        logPath,
        string.Concat(standardOutput, Environment.NewLine, standardError)).ConfigureAwait(false);
    if (exitCode != 0)
    {
        await Console.Error.WriteAsync(standardOutput).ConfigureAwait(false);
        await Console.Error.WriteAsync(standardError).ConfigureAwait(false);
        throw new InvalidOperationException(
            $"BenchmarkDotNet failed with exit code {exitCode}.");
    }
}

static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunProcessAsync(
    string executablePath,
    IReadOnlyList<string> arguments,
    string workingDirectory)
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
        ?? throw new InvalidOperationException($"The process did not start: {executablePath}");
    Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    return (
        process.ExitCode,
        await standardOutputTask.ConfigureAwait(false),
        await standardErrorTask.ConfigureAwait(false));
}
