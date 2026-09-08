namespace Csls.EndToEndPerformance;

/// <summary>
/// Identifies an explicitly selected debugger or evaluator worker used by the measurement.
/// </summary>
/// <param name="Variable">The environment variable selecting the worker.</param>
/// <param name="Path">The absolute worker executable or assembly path.</param>
/// <param name="Sha256">The selected file's content identity.</param>
internal sealed record DebuggerPerformanceWorker(string Variable, string Path, string Sha256);
