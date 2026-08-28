namespace Csls.Server;

/// <summary>
/// Identifies the current workspace lifecycle phase exposed through diagnostics.
/// </summary>
public enum ServerWorkspacePhase
{
    /// <summary>
    /// The server has not completed initialization.
    /// </summary>
    Uninitialized,

    /// <summary>
    /// The server has configuration but is waiting for initialization completion.
    /// </summary>
    Configured,

    /// <summary>
    /// The server is loading Roslyn workspace state.
    /// </summary>
    Loading,

    /// <summary>
    /// The workspace is ready for language requests.
    /// </summary>
    Ready,

    /// <summary>
    /// The server is tearing down the workspace.
    /// </summary>
    ShuttingDown
}
