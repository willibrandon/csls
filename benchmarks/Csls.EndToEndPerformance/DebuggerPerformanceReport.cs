namespace Csls.EndToEndPerformance;

/// <summary>
/// Identifies the measured executables, host, verified operations, and timing budget results.
/// </summary>
/// <param name="SchemaVersion">The debugger report schema version.</param>
/// <param name="CreatedAtUtc">The completion time of the run.</param>
/// <param name="Environment">The independently observed machine and toolchain metadata.</param>
/// <param name="ServerPath">The selected launcher path.</param>
/// <param name="ServerSha256">The measured launcher's content identity.</param>
/// <param name="WorkerOverrides">The explicitly selected worker paths and content identities.</param>
/// <param name="Configuration">The exact inputs, sampling policy, deadline, and timing budget.</param>
/// <param name="TargetPath">The compiled target assembly path.</param>
/// <param name="TargetSha256">The target assembly's content identity.</param>
/// <param name="SourcePath">The original source document used for the breakpoint.</param>
/// <param name="Measurements">The verified independent session measurements.</param>
/// <param name="Summary">The operation summaries grouped by request phase.</param>
/// <param name="BudgetViolations">Every operation median exceeding the requested budget.</param>
internal sealed record DebuggerPerformanceReport(int SchemaVersion, DateTimeOffset CreatedAtUtc,
    PerformanceEnvironment Environment, string ServerPath, string ServerSha256,
    IReadOnlyList<DebuggerPerformanceWorker> WorkerOverrides, DebuggerPerformanceOptions Configuration,
    string TargetPath, string TargetSha256, string SourcePath, IReadOnlyList<DebuggerPerformanceIteration> Measurements,
    IReadOnlyList<DebuggerPerformanceOperationSummary> Summary, IReadOnlyList<string> BudgetViolations)
{
    /// <summary>
    /// Gets whether every requested timing budget passed.
    /// </summary>
    public bool Passed => BudgetViolations.Count == 0;
}
