using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Retains one expandable managed value for a single debugger stop generation.
/// </summary>
internal sealed class ManagedValueHandle
{
    /// <summary>
    /// Gets or initializes the session-local variable-container identifier.
    /// </summary>
    internal required int Id { get; init; }

    /// <summary>
    /// Gets or initializes the stop generation that owns the value.
    /// </summary>
    internal required DebugStopGeneration Generation { get; init; }

    /// <summary>
    /// Gets or initializes the frame context used to evaluate source assignments.
    /// </summary>
    internal int? FrameId { get; init; }

    /// <summary>
    /// Gets or sets the managed thread inherited from the value's inspection context.
    /// </summary>
    internal int? ThreadId { get; set; }

    /// <summary>
    /// Gets or initializes the owned ICorDebugValue pointer.
    /// </summary>
    internal required nint Pointer { get; init; }

    /// <summary>
    /// Gets or initializes the owned canonical COM identity pointer.
    /// </summary>
    internal required nint Identity { get; init; }

    /// <summary>
    /// Gets or initializes the presentation view used to expand the value.
    /// </summary>
    internal ManagedValueView View { get; init; }

    /// <summary>
    /// Gets or sets the retained original object exposed by a successful proxy expansion.
    /// </summary>
    internal int ProxyRawValueReference { get; set; }

    /// <summary>
    /// Gets or sets evaluated debugger proxy properties published with this value.
    /// </summary>
    internal IReadOnlyList<ManagedDebuggerTypeProxyPropertyPresentation>? ProxyProperties
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the retained synthetic static-member container for this proxy.
    /// </summary>
    internal int ProxyStaticValueReference { get; set; }

    /// <summary>
    /// Gets or sets immutable rows owned by a synthetic variable container.
    /// </summary>
    internal IReadOnlyList<DebugVariableInfo>? SyntheticVariables { get; set; }

    /// <summary>
    /// Gets or initializes the opaque stopped-state memory handle.
    /// </summary>
    internal string? MemoryReference { get; init; }

    /// <summary>
    /// Gets or initializes the managed address represented by the memory handle.
    /// </summary>
    internal ulong MemoryAddress { get; init; }

    /// <summary>
    /// Gets or initializes the canonical source expression for child values.
    /// </summary>
    internal string? EvaluateName { get; init; }

    /// <summary>
    /// Gets or initializes tuple-name transforms for the exact declared type use.
    /// </summary>
    internal ManagedTupleCustomTypeInfo? TupleCustomTypeInfo { get; init; }
}
