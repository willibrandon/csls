using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Csls.EndToEndPerformance;

/// <summary>
/// Runs real csls process measurements, writes their report, and evaluates budgets.
/// </summary>
internal static class EndToEndPerformanceRunner
{
    private const double BytesPerMebibyte = 1024 * 1024;

    /// <summary>
    /// Executes every configured measurement and returns whether its budgets passed.
    /// </summary>
    /// <param name="options">The validated measurement configuration.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>Zero when every budget passes; otherwise one.</returns>
    internal static async Task<int> RunAsync(
        PerformanceOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        PerformanceEnvironment environment = await PerformanceEnvironmentReader.ReadAsync(
            cancellationToken).ConfigureAwait(false);
        var measurements = new List<PerformanceMeasurement>(options.Iterations);
        for (int iteration = 1; iteration <= options.Iterations; iteration++)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutSource.CancelAfter(options.Timeout);
            var session = LanguageServerMeasurementSession.Start(
                options.ServerPath,
                options.WorkspacePath);
            await using ConfiguredAsyncDisposable cleanup = session.ConfigureAwait(false);
            PerformanceMeasurement measurement = await session.MeasureAsync(
                iteration,
                options,
                timeoutSource.Token).ConfigureAwait(false);
            measurements.Add(measurement);
            WriteMeasurement(measurement);
        }

        var summary = PerformanceSummary.Create(measurements);
        IReadOnlyList<string> violations = EvaluateBudgets(options, summary);
        var report = new PerformanceReport
        {
            CreatedAtUtc = DateTimeOffset.UtcNow,
            RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            Environment = environment,
            ServerPath = options.ServerPath,
            McpServerPath = options.McpServerPath,
            WorkspacePath = options.WorkspacePath,
            ProbeDocumentPath = measurements[0].ProbeDocumentPath,
            ProbeAnalyzerNames = measurements[0].AnalyzerNames,
            IterationCount = options.Iterations,
            Commands =
            [
                "process/startup",
                "lsp/initialize",
                .. measurements[0].Operations.Select(static operation => operation.Name)
            ],
            Measurements = measurements,
            Summary = summary,
            BudgetViolations = violations
        };
        string? outputDirectory = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using FileStream output = new(
            options.OutputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(
            output,
            report,
            PerformanceReportJsonContext.Default.PerformanceReport,
            cancellationToken).ConfigureAwait(false);

        WriteSummary(summary, violations, options.OutputPath);
        return report.Passed ? 0 : 1;
    }

    private static List<string> EvaluateBudgets(
        PerformanceOptions options,
        PerformanceSummary summary)
    {
        var violations = new List<string>();
        AddTimingViolation(
            violations,
            "Median startup",
            summary.MedianStartupMilliseconds,
            options.StartupBudgetMilliseconds);
        AddTimingViolation(
            violations,
            "Median workspace load",
            summary.MedianWorkspaceLoadMilliseconds,
            options.WorkspaceLoadBudgetMilliseconds);
        AddTimingViolation(
            violations,
            "Median ready",
            summary.MedianReadyMilliseconds,
            options.ReadyBudgetMilliseconds);
        foreach (PerformanceOperationSummary operation in summary.Operations)
        {
            AddTimingViolation(
                violations,
                $"Median {operation.Name}",
                operation.MedianMilliseconds,
                options.OperationBudgetMilliseconds);
        }
        AddMemoryViolation(
            violations,
            "Maximum working set",
            summary.MaximumWorkingSetBytes,
            options.WorkingSetBudgetBytes);
        AddMemoryViolation(
            violations,
            "Maximum private memory",
            summary.MaximumPrivateMemoryBytes,
            options.PrivateMemoryBudgetBytes);
        if (summary.MaximumProcessCount > options.ProcessCountBudget)
        {
            violations.Add(
                $"Maximum process count {summary.MaximumProcessCount} exceeded " +
                $"{options.ProcessCountBudget}.");
        }

        return violations;
    }

    private static void AddTimingViolation(
        List<string> violations,
        string name,
        double actualMilliseconds,
        double budgetMilliseconds)
    {
        if (actualMilliseconds > budgetMilliseconds)
        {
            violations.Add(
                $"{name} {actualMilliseconds:F1} ms exceeded {budgetMilliseconds:F1} ms.");
        }
    }

    private static void AddMemoryViolation(
        List<string> violations,
        string name,
        long actualBytes,
        long budgetBytes)
    {
        if (actualBytes > budgetBytes)
        {
            violations.Add(
                $"{name} {ToMebibytes(actualBytes):F1} MiB exceeded " +
                $"{ToMebibytes(budgetBytes):F1} MiB.");
        }
    }

    private static void WriteMeasurement(PerformanceMeasurement measurement)
    {
        FormattableString message = $"Iteration {measurement.Iteration} ({measurement.CacheState}): startup {measurement.StartupMilliseconds:F1} ms, workspace {measurement.WorkspaceLoadMilliseconds:F1} ms, ready {measurement.ReadyMilliseconds:F1} ms, {measurement.ProjectCount} projects, {measurement.DocumentCount} documents, {measurement.AnalyzerReferenceCount} analyzers and generators, {measurement.ProcessCount} processes, {ToMebibytes(measurement.WorkingSetBytes):F1} MiB working set, {ToMebibytes(measurement.PrivateMemoryBytes):F1} MiB private memory, {measurement.ProcessorUtilizationPercent:F1}% CPU.";
        Console.WriteLine(message.ToString(CultureInfo.InvariantCulture));
        foreach (PerformanceOperation operation in measurement.Operations)
        {
            FormattableString timing = $"  {operation.Name}: {operation.Milliseconds:F1} ms";
            Console.WriteLine(timing.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void WriteSummary(
        PerformanceSummary summary,
        IReadOnlyList<string> violations,
        string outputPath)
    {
        FormattableString timings = $"Median: startup {summary.MedianStartupMilliseconds:F1} ms, workspace {summary.MedianWorkspaceLoadMilliseconds:F1} ms, ready {summary.MedianReadyMilliseconds:F1} ms.";
        Console.WriteLine(timings.ToString(CultureInfo.InvariantCulture));
        FormattableString resources = $"Maximum: {summary.MaximumProcessCount} processes, {ToMebibytes(summary.MaximumWorkingSetBytes):F1} MiB working set, {ToMebibytes(summary.MaximumPrivateMemoryBytes):F1} MiB private memory, {summary.MaximumProcessorUtilizationPercent:F1}% CPU.";
        Console.WriteLine(resources.ToString(CultureInfo.InvariantCulture));
        foreach (PerformanceOperationSummary operation in summary.Operations)
        {
            FormattableString timing = $"Median {operation.Name}: {operation.MedianMilliseconds:F1} ms";
            Console.WriteLine(timing.ToString(CultureInfo.InvariantCulture));
        }
        foreach (string violation in violations)
        {
            Console.Error.WriteLine(violation);
        }

        Console.WriteLine($"Report: {outputPath}");
    }

    private static double ToMebibytes(long bytes) => bytes / BytesPerMebibyte;
}
