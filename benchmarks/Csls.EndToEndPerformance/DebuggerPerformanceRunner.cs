using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Csls.EndToEndPerformance;

/// <summary>
/// Collects verified independent debugger sessions and publishes their phase-separated timing report.
/// </summary>
internal static class DebuggerPerformanceRunner
{
    /// <summary>
    /// Measures the selected debugger and reports every operation median exceeding the supplied budget.
    /// </summary>
    /// <param name="options">The executable inputs and operation budgets.</param>
    /// <param name="cancellationToken">Cancels the run and any active child process.</param>
    /// <returns>Zero for a completed run within budget, or one when a budget is exceeded.</returns>
    internal static async Task<int> RunAsync(DebuggerPerformanceOptions options, CancellationToken cancellationToken)
    {
        string? missingPath = new[] { options.ServerPath, options.TargetPath, options.SourcePath }.FirstOrDefault(path => !File.Exists(path));
        if (missingPath is not null)
        {
            throw new FileNotFoundException("A debugger measurement input was not found.", missingPath);
        }
        if (File.Exists(options.OutputPath) || Directory.Exists(options.OutputPath))
        {
            throw new IOException($"The measurement report path already exists: {options.OutputPath}");
        }
        string[] lines = await File.ReadAllLinesAsync(options.SourcePath, cancellationToken).ConfigureAwait(false);
        int[] sourceLines = [.. lines.Select((line, index) => (line, index)).Where(item =>
            item.line.EndsWith("// debugger performance stop", StringComparison.Ordinal)).Select(item => item.index + 1)];
        if (sourceLines is not [int sourceLine])
        {
            throw new InvalidDataException("The target source must contain exactly one debugger performance stop marker.");
        }
        PerformanceEnvironment environment = await PerformanceEnvironmentReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        string serverHash = await HashAsync(options.ServerPath, cancellationToken).ConfigureAwait(false);
        string targetHash = await HashAsync(options.TargetPath, cancellationToken).ConfigureAwait(false);
        var workers = new List<DebuggerPerformanceWorker>();
        foreach (string variable in new[] { "CSLS_DEBUGGER_WORKER_PATH", "CSLS_DEBUGGER_EVALUATOR_WORKER_PATH" })
        {
            string? configured = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string path = Path.GetFullPath(configured);
                workers.Add(new DebuggerPerformanceWorker(variable, path, await HashAsync(path, cancellationToken).ConfigureAwait(false)));
            }
        }
        var measurements = new List<DebuggerPerformanceIteration>();
        for (int iteration = 1; iteration <= options.Iterations; iteration++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            DebuggerPerformanceIteration measurement = await DebuggerMeasurementSession.MeasureAsync(
                options, iteration, sourceLine, timeout.Token).ConfigureAwait(false);
            measurements.Add(measurement);
            await Console.Error.WriteLineAsync($"Debugger session {iteration} completed; target {measurement.TargetProcessId} exited normally.")
                .ConfigureAwait(false);
        }
        var summary = new List<DebuggerPerformanceOperationSummary>();
        var violations = new List<string>();
        foreach (IGrouping<(string Name, string Phase), DebuggerPerformanceSample> group in
            measurements.SelectMany(item => item.Samples).GroupBy(item => (item.Name, item.Phase)))
        {
            double[] values = [.. group.Select(item => item.Milliseconds).Order()];
            int middle = values.Length / 2;
            double median = values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
            summary.Add(new DebuggerPerformanceOperationSummary(group.Key.Name, group.Key.Phase, values.Length, median, values[^1]));
            if (median > options.OperationBudgetMilliseconds)
            {
                violations.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{group.Key.Name} ({group.Key.Phase}) median {median:F3} ms exceeds {options.OperationBudgetMilliseconds:F3} ms."));
            }
        }
        var report = new DebuggerPerformanceReport(1, DateTimeOffset.UtcNow, environment, options.ServerPath, serverHash,
            workers, options, options.TargetPath, targetHash, options.SourcePath,
            measurements, summary, violations);
        string directory = Path.GetDirectoryName(options.OutputPath)
            ?? throw new InvalidOperationException("The report requires an output directory.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Join(directory, $".{Path.GetFileName(options.OutputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 16384, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, report, PerformanceReportJsonContext.Default.DebuggerPerformanceReport,
                    cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, options.OutputPath, overwrite: false);
        }
        finally
        {
            File.Delete(temporary);
        }
        await Console.Out.WriteLineAsync($"Debugger measurements: {options.OutputPath}").ConfigureAwait(false);
        foreach (string violation in violations)
        {
            await Console.Error.WriteLineAsync(violation).ConfigureAwait(false);
        }
        return report.Passed ? 0 : 1;
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 16384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
    }
}
