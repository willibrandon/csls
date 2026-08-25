#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

const double maximumRegressionRatio = 1.10;
string? baseBranch = null;
for (int index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--base-ref" when index + 1 < args.Length:
            baseBranch = args[++index];
            break;
        case "--help" or "-h" or "-?":
            await Console.Out.WriteLineAsync(
                "Runs stable benchmarks from a base branch and the current checkout on the same host.")
                .ConfigureAwait(false);
            await Console.Out.WriteLineAsync(
                "Usage: dotnet run --file scripts/Verify-BenchmarkRegression.cs -- --base-ref branch")
                .ConfigureAwait(false);
            return 0;
        default:
            await Console.Error.WriteLineAsync($"Unknown argument: {args[index]}")
                .ConfigureAwait(false);
            return 2;
    }
}

if (string.IsNullOrWhiteSpace(baseBranch) || !IsSafeBranchName(baseBranch))
{
    await Console.Error.WriteLineAsync("--base-ref must be a safe branch name.")
        .ConfigureAwait(false);
    return 2;
}

string repositoryRoot = ScriptSupport.FindRepositoryRoot();
string artifactRoot = Path.Join(repositoryRoot, "artifacts", "benchmark-regression");
string baselineBeforePath = Path.Join(artifactRoot, "baseline-before");
string candidatePath = Path.Join(artifactRoot, "candidate");
string baselineAfterPath = Path.Join(artifactRoot, "baseline-after");
string comparisonPath = Path.Join(artifactRoot, "comparison.md");
string worktreePath = Path.Join(
    Path.GetTempPath(),
    $"csls-benchmark-base-{Guid.NewGuid():N}");
bool worktreeCreated = false;

try
{
    if (Directory.Exists(artifactRoot))
    {
        Directory.Delete(artifactRoot, recursive: true);
    }

    Directory.CreateDirectory(artifactRoot);
    string dotnetPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    await RunCheckedAsync(
        "git",
        ["fetch", "--no-tags", "--depth", "1", "origin", baseBranch],
        repositoryRoot).ConfigureAwait(false);
    string baseCommit = (await RunCapturedAsync(
        "git",
        ["rev-parse", "--verify", $"origin/{baseBranch}^{{commit}}"],
        repositoryRoot).ConfigureAwait(false)).Trim();
    await RunCheckedAsync(
        "git",
        ["worktree", "add", "--detach", worktreePath, baseCommit],
        repositoryRoot).ConfigureAwait(false);
    worktreeCreated = true;

    await RunBenchmarksAsync(
        dotnetPath,
        worktreePath,
        baselineBeforePath).ConfigureAwait(false);
    await RunBenchmarksAsync(
        dotnetPath,
        repositoryRoot,
        candidatePath).ConfigureAwait(false);
    await RunBenchmarksAsync(
        dotnetPath,
        worktreePath,
        baselineAfterPath).ConfigureAwait(false);

    Dictionary<string, List<double>> baselineSamples = ReadSamples(baselineBeforePath);
    MergeSamples(baselineSamples, ReadSamples(baselineAfterPath));
    Dictionary<string, List<double>> candidateSamples = ReadSamples(candidatePath);
    if (!baselineSamples.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(candidateSamples.Keys))
    {
        throw new InvalidDataException(
            "The base and candidate benchmark sets do not contain the same cases.");
    }

    var report = new StringBuilder();
    report.AppendLine("# Benchmark regression check");
    report.AppendLine();
    report.AppendLine("| Benchmark | Baseline median | Candidate median | Change | Result |");
    report.AppendLine("| --- | ---: | ---: | ---: | --- |");
    bool regressionFound = false;
    foreach ((string benchmark, List<double> candidateValues) in candidateSamples
        .OrderBy(static item => item.Key, StringComparer.Ordinal))
    {
        List<double> baselineValues = baselineSamples[benchmark];
        double baselineMedian = GetPercentile(baselineValues, 0.50);
        double baselineUpperQuartile = GetPercentile(baselineValues, 0.75);
        double candidateMedian = GetPercentile(candidateValues, 0.50);
        double candidateLowerQuartile = GetPercentile(candidateValues, 0.25);
        double ratio = candidateMedian / baselineMedian;
        bool regressed = ratio > maximumRegressionRatio &&
            candidateLowerQuartile > baselineUpperQuartile;
        regressionFound |= regressed;
        report
            .Append("| ")
            .Append(EscapeMarkdown(benchmark))
            .Append(" | ")
            .Append(FormatNanoseconds(baselineMedian))
            .Append(" | ")
            .Append(FormatNanoseconds(candidateMedian))
            .Append(" | ")
            .Append(((ratio - 1) * 100).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture))
            .Append("% | ")
            .Append(regressed ? "regression" : "pass")
            .AppendLine(" |");
    }

    await File.WriteAllTextAsync(comparisonPath, report.ToString()).ConfigureAwait(false);
    await Console.Out.WriteAsync(report.ToString()).ConfigureAwait(false);
    return regressionFound ? 1 : 0;
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
finally
{
    if (worktreeCreated)
    {
        await RunIgnoringFailureAsync(
            "git",
            ["worktree", "remove", "--force", worktreePath],
            repositoryRoot).ConfigureAwait(false);
    }

    if (Directory.Exists(worktreePath))
    {
        Directory.Delete(worktreePath, recursive: true);
    }
}

static bool IsSafeBranchName(string value) =>
    value.Length <= 200 &&
    value[0] is not '-' &&
    !value.Contains("..", StringComparison.Ordinal) &&
    value.All(static character => char.IsAsciiLetterOrDigit(character) ||
        character is '/' or '-' or '_' or '.');

static async Task RunBenchmarksAsync(
    string dotnetPath,
    string checkoutPath,
    string artifactPath)
{
    string benchmarkProject = Path.Join(
        checkoutPath,
        "benchmarks",
        "Csls.Benchmarks",
        "Csls.Benchmarks.csproj");
    await RunCheckedAsync(
        dotnetPath,
        ["build", benchmarkProject, "--configuration", "Release"],
        checkoutPath).ConfigureAwait(false);
    await RunCheckedAsync(
        dotnetPath,
        [
            "run",
            "--project",
            benchmarkProject,
            "--configuration",
            "Release",
            "--no-build",
            "--",
            "--filter",
            "*DocumentUriBenchmarks*",
            "*ProtocolSerializationBenchmarks*",
            "*RequestSchedulerBenchmarks*",
            "--artifacts",
            artifactPath,
            "--noOverwrite",
            "--exporters",
            "fulljson",
            "--job",
            "Short"
        ],
        checkoutPath).ConfigureAwait(false);
}

static Dictionary<string, List<double>> ReadSamples(string artifactPath)
{
    var samples = new Dictionary<string, List<double>>(StringComparer.Ordinal);
    foreach (string reportPath in Directory.EnumerateFiles(
        artifactPath,
        "*-report-full.json",
        SearchOption.AllDirectories))
    {
        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        foreach (JsonElement benchmark in report.RootElement.GetProperty("Benchmarks")
            .EnumerateArray())
        {
            string fullName = benchmark.GetProperty("FullName").GetString()
                ?? throw new InvalidDataException($"Missing benchmark name in {reportPath}.");
            JsonElement values = benchmark
                .GetProperty("Statistics")
                .GetProperty("OriginalValues");
            if (!samples.TryGetValue(fullName, out List<double>? benchmarkSamples))
            {
                benchmarkSamples = [];
                samples.Add(fullName, benchmarkSamples);
            }

            benchmarkSamples.AddRange(values.EnumerateArray().Select(static value => value.GetDouble()));
        }
    }

    if (samples.Count == 0)
    {
        throw new InvalidDataException($"No benchmark reports were found under {artifactPath}.");
    }

    return samples;
}

static void MergeSamples(
    Dictionary<string, List<double>> destination,
    IReadOnlyDictionary<string, List<double>> source)
{
    foreach ((string benchmark, List<double> values) in source)
    {
        if (!destination.TryGetValue(benchmark, out List<double>? destinationValues))
        {
            destinationValues = [];
            destination.Add(benchmark, destinationValues);
        }

        destinationValues.AddRange(values);
    }
}

static double GetPercentile(IReadOnlyCollection<double> values, double percentile)
{
    if (values.Count == 0)
    {
        throw new InvalidDataException("A benchmark report contained no measurements.");
    }

    double[] ordered = [.. values.Order()];
    double index = percentile * (ordered.Length - 1);
    int lowerIndex = (int)Math.Floor(index);
    int upperIndex = (int)Math.Ceiling(index);
    double fraction = index - lowerIndex;
    return ordered[lowerIndex] + ((ordered[upperIndex] - ordered[lowerIndex]) * fraction);
}

static string FormatNanoseconds(double value) =>
    value.ToString("N1", CultureInfo.InvariantCulture) + " ns";

static string EscapeMarkdown(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);

static async Task RunCheckedAsync(
    string executablePath,
    IReadOnlyList<string> arguments,
    string workingDirectory)
{
    int exitCode = await RunProcessAsync(
        executablePath,
        arguments,
        workingDirectory,
        redirectOutput: false).ConfigureAwait(false);
    if (exitCode != 0)
    {
        throw new InvalidOperationException(
            $"{executablePath} failed with exit code {exitCode}.");
    }
}

static async Task<string> RunCapturedAsync(
    string executablePath,
    IReadOnlyList<string> arguments,
    string workingDirectory)
{
    ProcessStartInfo startInfo = CreateStartInfo(
        executablePath,
        arguments,
        workingDirectory,
        redirectOutput: true);
    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"The process did not start: {executablePath}");
    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string output = await outputTask.ConfigureAwait(false);
    string error = await errorTask.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"{executablePath} failed with exit code {process.ExitCode}: {error.Trim()}");
    }

    return output;
}

static async Task RunIgnoringFailureAsync(
    string executablePath,
    IReadOnlyList<string> arguments,
    string workingDirectory) =>
    _ = await RunProcessAsync(
        executablePath,
        arguments,
        workingDirectory,
        redirectOutput: false).ConfigureAwait(false);

static async Task<int> RunProcessAsync(
    string executablePath,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    bool redirectOutput)
{
    ProcessStartInfo startInfo = CreateStartInfo(
        executablePath,
        arguments,
        workingDirectory,
        redirectOutput);
    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"The process did not start: {executablePath}");
    await process.WaitForExitAsync().ConfigureAwait(false);
    return process.ExitCode;
}

static ProcessStartInfo CreateStartInfo(
    string executablePath,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    bool redirectOutput)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = executablePath,
        RedirectStandardError = redirectOutput,
        RedirectStandardOutput = redirectOutput,
        UseShellExecute = false,
        WorkingDirectory = workingDirectory
    };
    startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
    startInfo.Environment["DOTNET_NOLOGO"] = "1";
    foreach (string argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    return startInfo;
}
