using System.Collections.Immutable;

namespace Csls.Debugger.Terminal;

/// <summary>
/// Holds one immutable, consistently published frame of debugger terminal presentation data.
/// </summary>
internal sealed record DebuggerTerminalViewSnapshot
{
    /// <summary>
    /// Gets the fully formatted session, module, exception, and status header.
    /// </summary>
    internal string Header { get; init; } =
        "csls debugger  managed target  pid -  Created  0 modules";

    /// <summary>
    /// Gets the source row mapping revision shared by cursor-only presentation updates.
    /// </summary>
    internal long SourceRevision { get; init; }

    /// <summary>
    /// Gets the source rows captured for this frame.
    /// </summary>
    internal ImmutableArray<string> SourceLines { get; init; } =
        ["Waiting for a managed stop."];

    /// <summary>
    /// Gets the focused source row captured with the source content.
    /// </summary>
    internal int SourceFocusedIndex { get; init; }

    /// <summary>
    /// Gets the thread identifier mapping revision shared by selection-only presentation updates.
    /// </summary>
    internal long ThreadRevision { get; init; }

    /// <summary>
    /// Gets the managed thread rows captured for this frame.
    /// </summary>
    internal ImmutableArray<string> ThreadLines { get; init; } = [];

    /// <summary>
    /// Gets the selected row within the captured thread list.
    /// </summary>
    internal int SelectedThreadIndex { get; init; }

    /// <summary>
    /// Gets the frame identifier mapping revision shared by selection-only presentation updates.
    /// </summary>
    internal long StackRevision { get; init; }

    /// <summary>
    /// Gets the managed stack rows captured for this frame.
    /// </summary>
    internal ImmutableArray<string> StackLines { get; init; } = [];

    /// <summary>
    /// Gets the selected row within the captured stack list.
    /// </summary>
    internal int SelectedStackFrameIndex { get; init; }

    /// <summary>
    /// Gets the argument and local rows captured for this frame.
    /// </summary>
    internal ImmutableArray<string> VariableLines { get; init; } = [];

    /// <summary>
    /// Gets the auxiliary pane title captured with its rows.
    /// </summary>
    internal string AuxiliaryTitle { get; init; } = "Target Output";

    /// <summary>
    /// Gets the selected auxiliary pane rows captured for this frame.
    /// </summary>
    internal ImmutableArray<string> AuxiliaryLines { get; init; } = ["No target output."];
}
