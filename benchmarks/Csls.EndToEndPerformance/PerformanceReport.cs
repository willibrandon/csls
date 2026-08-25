namespace Csls.EndToEndPerformance;

/// <summary>
/// Contains one versioned end-to-end performance report and budget result.
/// </summary>
internal sealed class PerformanceReport
{
    /// <summary>
    /// Gets the report schema version.
    /// </summary>
    public int SchemaVersion { get; init; } = 2;

    /// <summary>
    /// Gets the UTC time at which the report was completed.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// Gets the measured runtime identifier.
    /// </summary>
    public required string RuntimeIdentifier { get; init; }

    /// <summary>
    /// Gets the complete machine and .NET toolchain metadata.
    /// </summary>
    public required PerformanceEnvironment Environment { get; init; }

    /// <summary>
    /// Gets the absolute measured csls launcher path.
    /// </summary>
    public required string ServerPath { get; init; }

    /// <summary>
    /// Gets the absolute measured csls-mcp launcher path.
    /// </summary>
    public required string McpServerPath { get; init; }

    /// <summary>
    /// Gets the absolute measured workspace path.
    /// </summary>
    public required string WorkspacePath { get; init; }

    /// <summary>
    /// Gets the real source document used for protocol operations.
    /// </summary>
    public required string ProbeDocumentPath { get; init; }

    /// <summary>
    /// Gets the analyzer and source-generator assemblies loaded for the probe project.
    /// </summary>
    public required IReadOnlyList<string> ProbeAnalyzerNames { get; init; }

    /// <summary>
    /// Gets the requested number of fresh process iterations.
    /// </summary>
    public int IterationCount { get; init; }

    /// <summary>
    /// Gets the ordered real-product command and protocol operation names.
    /// </summary>
    public required IReadOnlyList<string> Commands { get; init; }

    /// <summary>
    /// Gets every fresh process measurement in execution order.
    /// </summary>
    public required IReadOnlyList<PerformanceMeasurement> Measurements { get; init; }

    /// <summary>
    /// Gets the aggregate timing and resource measurements.
    /// </summary>
    public required PerformanceSummary Summary { get; init; }

    /// <summary>
    /// Gets every exceeded performance budget.
    /// </summary>
    public required IReadOnlyList<string> BudgetViolations { get; init; }

    /// <summary>
    /// Gets whether every configured performance budget passed.
    /// </summary>
    public bool Passed => BudgetViolations.Count == 0;
}
