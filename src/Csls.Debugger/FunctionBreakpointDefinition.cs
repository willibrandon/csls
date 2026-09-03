using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Tracks one logical managed function breakpoint across loaded modules.
/// </summary>
internal sealed class FunctionBreakpointDefinition
{
    /// <summary>
    /// Gets the stable session-local breakpoint identifier.
    /// </summary>
    internal required int Id { get; init; }

    /// <summary>
    /// Gets the normalized requested function name.
    /// </summary>
    internal required string Name { get; init; }

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
        BindingCount == 0
            ? "The breakpoint is pending until a matching managed function is loaded."
            : null);
}
