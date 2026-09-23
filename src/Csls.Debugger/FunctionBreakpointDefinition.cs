using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Tracks one logical managed function breakpoint across loaded modules.
/// </summary>
internal sealed class FunctionBreakpointDefinition : IManagedBreakpointDefinition
{
    /// <summary>
    /// Gets the stable session-local breakpoint identifier.
    /// </summary>
    internal required int Id { get; init; }

    /// <summary>
    /// Gets the normalized requested function name.
    /// </summary>
    internal required string Name { get; init; }

    /// <inheritdoc />
    public string? Condition { get; init; }

    /// <inheritdoc />
    public string? LogMessage => null;

    /// <summary>
    /// Gets the optional validated hit-count predicate.
    /// </summary>
    internal DebugHitCondition? HitCondition { get; init; }

    /// <summary>
    /// Gets a request-validation failure that prevents runtime binding.
    /// </summary>
    internal string? ValidationMessage { get; init; }

    /// <summary>
    /// Gets or sets the number of active runtime method bindings.
    /// </summary>
    internal int BindingCount { get; set; }

    /// <summary>
    /// Creates the externally visible function-breakpoint snapshot.
    /// </summary>
    /// <returns>The current immutable binding state.</returns>
    internal DebugFunctionBreakpointInfo ToInfo() => new(
        Id,
        Name,
        BindingCount > 0,
        ValidationMessage ?? (BindingCount == 0
            ? "The breakpoint is pending until a matching managed function is loaded."
            : null),
        Condition,
        HitCondition?.Expression);

    /// <summary>
    /// Records one runtime hit and determines whether the target should stop.
    /// </summary>
    /// <returns>True when the breakpoint has no hit condition or its predicate matches.</returns>
    public bool RegisterHit() => HitCondition?.RegisterHit() ?? true;
}
