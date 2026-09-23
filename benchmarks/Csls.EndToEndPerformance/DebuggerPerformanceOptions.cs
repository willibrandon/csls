namespace Csls.EndToEndPerformance;

/// <summary>
/// Defines the executable inputs and sampling policy for a real debugger workload.
/// </summary>
/// <param name="ServerPath">The selected csls executable or managed launcher assembly.</param>
/// <param name="TargetPath">The compiled measurement target assembly.</param>
/// <param name="SourcePath">The target's original source document.</param>
/// <param name="OutputPath">The new report file.</param>
/// <param name="Iterations">The number of independently launched debugger sessions.</param>
/// <param name="Samples">The repeated measurements after each first stopped-state request.</param>
/// <param name="Timeout">The deadline for each independently launched session.</param>
/// <param name="OperationBudgetMilliseconds">The maximum median operation duration.</param>
internal sealed record DebuggerPerformanceOptions(string ServerPath, string TargetPath, string SourcePath,
    string OutputPath, int Iterations, int Samples, TimeSpan Timeout, double OperationBudgetMilliseconds);
