namespace Csls.Debugger.Contracts;

/// <summary>
/// Replaces the complete source-breakpoint set for one document.
/// </summary>
/// <param name="SourcePath">The absolute source document path.</param>
/// <param name="Breakpoints">The ordered replacement breakpoint set.</param>
public sealed record DebugSourceBreakpointSetRequest(
    string SourcePath,
    IReadOnlyList<DebugSourceBreakpointRequest> Breakpoints);
