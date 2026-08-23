namespace Csls.Dashboard;

/// <summary>
/// Identifies one confirmed workspace mutation available from the live dashboard.
/// </summary>
internal enum DashboardOperation
{
    /// <summary>
    /// Restores all workspace entry points and reloads their Roslyn state.
    /// </summary>
    Restore,

    /// <summary>
    /// Reloads all workspace roots while preserving unsaved document overlays.
    /// </summary>
    Reload,

    /// <summary>
    /// Recreates all Roslyn workspace hosts while preserving unsaved document overlays.
    /// </summary>
    RestartBuildHosts,

    /// <summary>
    /// Removes retained diagnostic, semantic-token, and pending-edit results.
    /// </summary>
    ClearCaches
}
