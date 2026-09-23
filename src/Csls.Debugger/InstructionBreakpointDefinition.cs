using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Tracks one logical managed-IL breakpoint across matching runtime modules.
/// </summary>
internal sealed class InstructionBreakpointDefinition : IManagedBreakpointDefinition
{
    /// <summary>
    /// Gets the stable session-local identifier.
    /// </summary>
    internal required int Id { get; init; }

    /// <summary>
    /// Gets the original instruction reference.
    /// </summary>
    internal required string InstructionReference { get; init; }

    /// <summary>
    /// Gets the requested signed byte offset.
    /// </summary>
    internal required long Offset { get; init; }

    /// <summary>
    /// Gets the normalized module path when the reference resolved.
    /// </summary>
    internal string? ModulePath { get; init; }

    /// <summary>
    /// Gets the stable session-local module identifier when resolution succeeded.
    /// </summary>
    internal int? ModuleId { get; init; }

    /// <summary>
    /// Gets the managed method token when the reference resolved.
    /// </summary>
    internal uint MethodToken { get; init; }

    /// <summary>
    /// Gets the exact managed-IL offset when the reference resolved.
    /// </summary>
    internal uint IlOffset { get; init; }

    /// <inheritdoc />
    public string? Condition { get; init; }

    /// <inheritdoc />
    public string? LogMessage => null;

    /// <summary>
    /// Gets the optional validated hit-count predicate.
    /// </summary>
    internal DebugHitCondition? HitCondition { get; init; }

    /// <summary>
    /// Gets an address or request validation failure.
    /// </summary>
    internal string? ValidationMessage { get; init; }

    /// <summary>
    /// Gets or sets the latest runtime binding diagnostic.
    /// </summary>
    internal string? BindingMessage { get; set; }

    /// <summary>
    /// Gets or sets the number of active runtime bindings.
    /// </summary>
    internal int BindingCount { get; set; }

    /// <summary>
    /// Records one runtime hit and evaluates the optional hit-count predicate.
    /// </summary>
    /// <returns>True when the target should stop.</returns>
    public bool RegisterHit() => HitCondition?.RegisterHit() ?? true;

    /// <summary>
    /// Creates the externally visible breakpoint state.
    /// </summary>
    /// <returns>The immutable current breakpoint state.</returns>
    internal DebugInstructionBreakpointInfo ToInfo() => new(
        Id,
        BindingCount > 0 && ValidationMessage is null,
        InstructionReference,
        Offset,
        ValidationMessage ?? BindingMessage ?? (BindingCount == 0
            ? "The managed-IL breakpoint is pending until its module is loaded."
            : null),
        Condition,
        HitCondition?.Expression);
}
