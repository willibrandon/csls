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
string candidateConfirmationPath = Path.Join(artifactRoot, "candidate-confirmation");
string baselineConfirmationPath = Path.Join(artifactRoot, "baseline-confirmation");
string comparisonPath = Path.Join(artifactRoot, "comparison.md");
string repositoryParent = Directory.GetParent(repositoryRoot)?.FullName ??
    throw new DirectoryNotFoundException("The repository parent directory was not found.");
string baselineSourcePath = Path.Join(
    repositoryParent,
    $".csls-benchmark-base-{Guid.NewGuid():N}");

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
    string originUrl = (await RunCapturedAsync(
        "git",
        ["remote", "get-url", "origin"],
        repositoryRoot).ConfigureAwait(false)).Trim();
    string changedPathOutput = await RunCapturedAsync(
        "git",
        ["diff", "--name-only", "--diff-filter=ACMRT", baseCommit, "HEAD"],
        repositoryRoot).ConfigureAwait(false);
    string[] changedPaths = changedPathOutput.Split(
        ['\r', '\n'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    IReadOnlyList<string> benchmarkFilters = SelectBenchmarkFilters(changedPaths);
    if (benchmarkFilters.Count == 0)
    {
        const string noChangesReport =
            "# Benchmark regression check\n\nNo stable benchmark dependencies changed.\n";
        await File.WriteAllTextAsync(comparisonPath, noChangesReport).ConfigureAwait(false);
        await Console.Out.WriteAsync(noChangesReport).ConfigureAwait(false);
        return 0;
    }

    await RunCheckedAsync(
        "git",
        [
            "clone",
            "--no-checkout",
            "--depth",
            "1",
            "--branch",
            baseBranch,
            originUrl,
            baselineSourcePath
        ],
        repositoryParent).ConfigureAwait(false);
    await RunCheckedAsync(
        "git",
        ["-C", baselineSourcePath, "switch", "--detach", baseCommit],
        repositoryParent).ConfigureAwait(false);

    await RunBenchmarksAsync(
        dotnetPath,
        baselineSourcePath,
        baselineBeforePath,
        benchmarkFilters,
        "Short").ConfigureAwait(false);
    await RunBenchmarksAsync(
        dotnetPath,
        repositoryRoot,
        candidatePath,
        benchmarkFilters,
        "Short").ConfigureAwait(false);
    await RunBenchmarksAsync(
        dotnetPath,
        baselineSourcePath,
        baselineAfterPath,
        benchmarkFilters,
        "Short").ConfigureAwait(false);

    Dictionary<string, List<double>> baselineSamples = ReadSamples(baselineBeforePath);
    MergeSamples(baselineSamples, ReadSamples(baselineAfterPath));
    Dictionary<string, List<double>> candidateSamples = ReadSamples(candidatePath);
    if (!baselineSamples.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(candidateSamples.Keys))
    {
        throw new InvalidDataException(
            "The base and candidate benchmark sets do not contain the same cases.");
    }

    HashSet<string> initialRegressions = FindRegressions(
        baselineSamples,
        candidateSamples,
        maximumRegressionRatio);
    if (initialRegressions.Count > 0)
    {
        await Console.Out.WriteLineAsync(
            "Confirming the initial regression signal with longer targeted measurements.")
            .ConfigureAwait(false);
        IReadOnlyList<string> confirmationFilters = CreateConfirmationFilters(
            initialRegressions);
        await RunBenchmarksAsync(
            dotnetPath,
            repositoryRoot,
            candidateConfirmationPath,
            confirmationFilters,
            "Medium").ConfigureAwait(false);
        await RunBenchmarksAsync(
            dotnetPath,
            baselineSourcePath,
            baselineConfirmationPath,
            confirmationFilters,
            "Medium").ConfigureAwait(false);
        Dictionary<string, List<double>> candidateConfirmationSamples =
            ReadSamples(candidateConfirmationPath);
        Dictionary<string, List<double>> baselineConfirmationSamples =
            ReadSamples(baselineConfirmationPath);
        foreach (string benchmark in initialRegressions)
        {
            if (!candidateConfirmationSamples.TryGetValue(
                    benchmark,
                    out List<double>? candidateValues) ||
                !baselineConfirmationSamples.TryGetValue(
                    benchmark,
                    out List<double>? baselineValues))
            {
                throw new InvalidDataException(
                    $"Confirmation results did not include {benchmark}.");
            }

            candidateSamples[benchmark] = candidateValues;
            baselineSamples[benchmark] = baselineValues;
        }
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
    if (Directory.Exists(baselineSourcePath))
    {
        Directory.Delete(baselineSourcePath, recursive: true);
    }
}

static HashSet<string> FindRegressions(
    IReadOnlyDictionary<string, List<double>> baselineSamples,
    IReadOnlyDictionary<string, List<double>> candidateSamples,
    double maximumRatio)
{
    var regressions = new HashSet<string>(StringComparer.Ordinal);
    foreach ((string benchmark, List<double> candidateValues) in candidateSamples)
    {
        List<double> baselineValues = baselineSamples[benchmark];
        double baselineMedian = GetPercentile(baselineValues, 0.50);
        double baselineUpperQuartile = GetPercentile(baselineValues, 0.75);
        double candidateMedian = GetPercentile(candidateValues, 0.50);
        double candidateLowerQuartile = GetPercentile(candidateValues, 0.25);
        if (candidateMedian / baselineMedian > maximumRatio &&
            candidateLowerQuartile > baselineUpperQuartile)
        {
            regressions.Add(benchmark);
        }
    }

    return regressions;
}

static IReadOnlyList<string> CreateConfirmationFilters(
    IEnumerable<string> benchmarkNames) =>
    [
        .. benchmarkNames
            .Select(static name =>
            {
                int parameterIndex = name.IndexOf('(', StringComparison.Ordinal);
                string methodName = parameterIndex >= 0 ? name[..parameterIndex] : name;
                return $"*{methodName}*";
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

static IReadOnlyList<string> SelectBenchmarkFilters(IEnumerable<string> changedPaths)
{
    const string documentUriFilter = "*DocumentUriBenchmarks*";
    const string protocolSerializationFilter = "*ProtocolSerializationBenchmarks*";
    const string requestSchedulerFilter = "*RequestSchedulerBenchmarks*";
    string[] allFilters =
    [
        documentUriFilter,
        protocolSerializationFilter,
        requestSchedulerFilter
    ];
    var selectedFilters = new HashSet<string>(StringComparer.Ordinal);
    foreach (string path in changedPaths)
    {
        if (IsBenchmarkInfrastructure(path))
        {
            return allFilters;
        }

        if (path.StartsWith("src/Csls.Protocol/", StringComparison.Ordinal))
        {
            selectedFilters.Add(documentUriFilter);
            selectedFilters.Add(protocolSerializationFilter);
        }

        if (path.StartsWith("src/Csls.Core/", StringComparison.Ordinal))
        {
            selectedFilters.Add(requestSchedulerFilter);
        }
    }

    return [.. selectedFilters.Order(StringComparer.Ordinal)];
}

static bool IsBenchmarkInfrastructure(string path) =>
    path.StartsWith("benchmarks/", StringComparison.Ordinal) ||
    path is
        ".github/workflows/benchmarks.yml" or
        "Directory.Build.props" or
        "Directory.Build.targets" or
        "Directory.Packages.props" or
        "NuGet.config" or
        "global.json" or
        "scripts/Verify-BenchmarkRegression.cs";

static bool IsSafeBranchName(string value) =>
    value.Length <= 200 &&
    value[0] is not '-' &&
    !value.Contains("..", StringComparison.Ordinal) &&
    value.All(static character => char.IsAsciiLetterOrDigit(character) ||
        character is '/' or '-' or '_' or '.');

static async Task RunBenchmarksAsync(
    string dotnetPath,
    string checkoutPath,
    string artifactPath,
    IReadOnlyList<string> benchmarkFilters,
    string job)
{
    ArgumentOutOfRangeException.ThrowIfZero(benchmarkFilters.Count);
    Directory.CreateDirectory(artifactPath);
    string binlogDirectory = Path.Join(artifactPath, "binlogs");
    Directory.CreateDirectory(binlogDirectory);
    string benchmarkProject = Path.Join(
        checkoutPath,
        "benchmarks",
        "Csls.Benchmarks",
        "Csls.Benchmarks.csproj");
    await RunCheckedAsync(
        dotnetPath,
        [
            "build",
            benchmarkProject,
            "--configuration",
            "Release",
            $"--binaryLogger:{Path.Join(binlogDirectory, "build.binlog")}"
        ],
        checkoutPath).ConfigureAwait(false);
    List<string> arguments =
    [
        "run",
        "--project",
        benchmarkProject,
        "--configuration",
        "Release",
        "--no-build",
        "--",
        "--filter"
    ];
    arguments.AddRange(benchmarkFilters);
    arguments.AddRange(
    [
        "--artifacts",
        artifactPath,
        "--noOverwrite",
        "--exporters",
        "fulljson",
        "--inProcess",
        "--job",
        job
    ]);
    await RunCheckedAsync(
        dotnetPath,
        arguments,
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
