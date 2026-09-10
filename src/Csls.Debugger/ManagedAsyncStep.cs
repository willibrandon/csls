using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Owns one temporary runtime breakpoint used to follow an asynchronous continuation.
/// </summary>
internal sealed class ManagedAsyncStep
{
    /// <summary>
    /// Gets or sets the owned runtime breakpoint pointer.
    /// </summary>
    internal required nint Breakpoint { get; set; }

    /// <summary>
    /// Gets or sets the owned canonical breakpoint identity.
    /// </summary>
    internal required nint Identity { get; set; }

    /// <summary>
    /// Gets or initializes the owned runtime module pointer.
    /// </summary>
    internal required nint Module { get; init; }

    /// <summary>
    /// Gets or initializes the optional strong handle for the selected state-machine instance.
    /// </summary>
    internal required nint StateMachineHandle { get; init; }

    /// <summary>
    /// Gets the managed thread that began the step.
    /// </summary>
    internal required int InitialThreadId { get; init; }

    /// <summary>
    /// Gets the source-level operation to resume after asynchronous suspension.
    /// </summary>
    internal required DebugStepKind Kind { get; init; }

    /// <summary>
    /// Gets the method containing the continuation breakpoint.
    /// </summary>
    internal required uint ResumeMethodToken { get; init; }

    /// <summary>
    /// Gets the continuation breakpoint IL offset.
    /// </summary>
    internal required uint ResumeOffset { get; init; }

    /// <summary>
    /// Gets or sets whether the yield breakpoint has advanced to the continuation breakpoint.
    /// </summary>
    internal bool WaitsForResume { get; set; }
}
