namespace Csls.Debugger;

/// <summary>
/// Owns one debugger proxy construction and serial property-presentation operation.
/// </summary>
internal sealed class ManagedDebuggerTypeProxyEvaluation
{
    /// <summary>
    /// Creates proxy presentation state for one original runtime value.
    /// </summary>
    /// <param name="evaluateName">The original source expression when available.</param>
    /// <param name="threadId">The managed thread selected for target execution.</param>
    internal ManagedDebuggerTypeProxyEvaluation(string? evaluateName, int threadId)
    {
        EvaluateName = evaluateName;
        ThreadId = threadId;
    }

    /// <summary>
    /// Gets the original source expression when one is available.
    /// </summary>
    internal string? EvaluateName { get; }

    /// <summary>
    /// Gets the managed thread selected for target execution.
    /// </summary>
    internal int ThreadId { get; }

    /// <summary>
    /// Gets or sets whether CoreCLR completed the proxy constructor.
    /// </summary>
    internal bool ConstructorCompleted { get; set; }

    /// <summary>
    /// Gets the owned property getter bindings awaiting execution or release.
    /// </summary>
    internal List<ManagedDebuggerTypeProxyPropertyBinding> Properties { get; } = [];

    /// <summary>
    /// Gets the evaluated property results awaiting final-generation publication.
    /// </summary>
    internal List<ManagedDebuggerTypeProxyPropertyResult> PropertyResults { get; } = [];

    /// <summary>
    /// Gets or sets the next property getter index.
    /// </summary>
    internal int NextPropertyIndex { get; set; }

    /// <summary>
    /// Gets or sets the property whose getter currently executes.
    /// </summary>
    internal ManagedDebuggerTypeProxyPropertyBinding? CurrentProperty { get; set; }
}
