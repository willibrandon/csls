using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Tracks one logical source breakpoint independently of runtime module bindings.
/// </summary>
internal sealed class SourceBreakpointDefinition
{
    /// <summary>
    /// Gets the stable session-local identifier.
    /// </summary>
    internal required int Id { get; init; }

    /// <summary>
    /// Gets the normalized absolute source path.
    /// </summary>
    internal required string SourcePath { get; init; }

    /// <summary>
    /// Gets the requested one-based source line.
    /// </summary>
    internal required int RequestedLine { get; init; }

    /// <summary>
    /// Gets the optional requested one-based source column.
    /// </summary>
    internal required int? RequestedColumn { get; init; }

    /// <summary>
    /// Gets or sets the resolved one-based source line.
    /// </summary>
    internal int? ResolvedLine { get; set; }

    /// <summary>
    /// Gets or sets the resolved one-based source column.
    /// </summary>
    internal int? ResolvedColumn { get; set; }

    /// <summary>
    /// Creates the externally visible binding snapshot.
    /// </summary>
    /// <returns>The current immutable breakpoint state.</returns>
    internal DebugSourceBreakpointInfo ToInfo() => new(
        Id,
        SourcePath,
        ResolvedLine is not null,
        ResolvedLine ?? RequestedLine,
        ResolvedColumn ?? RequestedColumn,
        ResolvedLine is null
            ? "The breakpoint is pending until a matching module and Portable PDB are loaded."
            : null);
}
