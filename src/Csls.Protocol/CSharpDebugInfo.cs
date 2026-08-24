namespace Csls.Protocol;

/// <summary>
/// Describes live language-server state for diagnostics and test synchronization.
/// </summary>
public sealed class CSharpDebugInfo
{
    /// <summary>
    /// Gets the current workspace lifecycle and loaded folders.
    /// </summary>
    public required CSharpDebugWorkspaceInfo Workspace { get; init; }

    /// <summary>
    /// Gets the current bounded scheduler state and cumulative timings.
    /// </summary>
    public required CSharpDebugRequestQueueInfo RequestQueue { get; init; }
}
