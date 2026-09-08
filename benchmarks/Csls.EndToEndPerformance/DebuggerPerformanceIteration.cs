namespace Csls.EndToEndPerformance;

/// <summary>
/// Captures one independently launched debugger session and its verified target result.
/// </summary>
/// <param name="Iteration">The one-based fresh-process iteration.</param>
/// <param name="ProcessState">Whether this is the first or a subsequent process in this run.</param>
/// <param name="AdapterProcessId">The measured launcher process identifier.</param>
/// <param name="TargetProcessId">The identifier announced by the real DAP process event.</param>
/// <param name="AdapterStartedAtUtc">The launcher's operating-system creation time.</param>
/// <param name="TargetStartedAtUtc">The target's operating-system creation time observed while stopped.</param>
/// <param name="Samples">The completed operation measurements in execution order.</param>
/// <param name="Resources">The independently observed process-tree resource checkpoints.</param>
/// <param name="Output">The target's verified output.</param>
/// <param name="TargetExitCode">The target exit code announced through DAP.</param>
/// <param name="AdapterExitCode">The observed launcher exit code.</param>
internal sealed record DebuggerPerformanceIteration(int Iteration, string ProcessState, int AdapterProcessId,
    int TargetProcessId, DateTimeOffset AdapterStartedAtUtc, DateTimeOffset TargetStartedAtUtc, IReadOnlyList<DebuggerPerformanceSample> Samples,
    IReadOnlyList<DebuggerPerformanceResources> Resources, string Output, int TargetExitCode, int AdapterExitCode);
