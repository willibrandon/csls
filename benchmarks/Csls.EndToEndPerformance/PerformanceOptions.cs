namespace Csls.EndToEndPerformance;

/// <summary>
/// Contains one end-to-end performance run configuration.
/// </summary>
internal sealed class PerformanceOptions
{
    /// <summary>
    /// Gets the absolute published csls launcher path.
    /// </summary>
    internal required string ServerPath { get; init; }

    /// <summary>
    /// Gets the absolute workspace path measured by every iteration.
    /// </summary>
    internal required string WorkspacePath { get; init; }

    /// <summary>
    /// Gets the absolute JSON report path.
    /// </summary>
    internal required string OutputPath { get; init; }

    /// <summary>
    /// Gets the number of fresh process iterations to execute.
    /// </summary>
    internal int Iterations { get; init; }

    /// <summary>
    /// Gets the maximum duration allowed for each iteration.
    /// </summary>
    internal TimeSpan Timeout { get; init; }

    /// <summary>
    /// Gets the maximum median protocol startup time.
    /// </summary>
    internal double StartupBudgetMilliseconds { get; init; }

    /// <summary>
    /// Gets the maximum median workspace-load time.
    /// </summary>
    internal double WorkspaceLoadBudgetMilliseconds { get; init; }

    /// <summary>
    /// Gets the maximum median total ready time.
    /// </summary>
    internal double ReadyBudgetMilliseconds { get; init; }

    /// <summary>
    /// Gets the maximum ready-state process-tree working set.
    /// </summary>
    internal long WorkingSetBudgetBytes { get; init; }

    /// <summary>
    /// Gets the maximum ready-state process-tree private memory.
    /// </summary>
    internal long PrivateMemoryBudgetBytes { get; init; }

    /// <summary>
    /// Gets the maximum ready-state process-tree count.
    /// </summary>
    internal int ProcessCountBudget { get; init; }
}
