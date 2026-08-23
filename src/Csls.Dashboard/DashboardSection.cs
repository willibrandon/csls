namespace Csls.Dashboard;

/// <summary>
/// Identifies one live-session data view in the Hex1b dashboard.
/// </summary>
internal enum DashboardSection
{
    /// <summary>
    /// Shows discovered live language-server sessions.
    /// </summary>
    Sessions,

    /// <summary>
    /// Shows confirmed workspace maintenance operations.
    /// </summary>
    Actions,

    /// <summary>
    /// Shows loaded workspace folders.
    /// </summary>
    Workspaces,

    /// <summary>
    /// Shows loaded Roslyn projects.
    /// </summary>
    Projects,

    /// <summary>
    /// Shows loaded source documents.
    /// </summary>
    Documents,

    /// <summary>
    /// Shows current compiler and analyzer diagnostics.
    /// </summary>
    Diagnostics,

    /// <summary>
    /// Shows request scheduler totals and active work.
    /// </summary>
    Requests,

    /// <summary>
    /// Shows bounded scheduler queue state.
    /// </summary>
    Queues,

    /// <summary>
    /// Shows active Roslyn workspace hosts.
    /// </summary>
    BuildHosts,

    /// <summary>
    /// Shows bounded session caches.
    /// </summary>
    Caches,

    /// <summary>
    /// Shows recent structured worker logs.
    /// </summary>
    Logs,

    /// <summary>
    /// Shows the active or most recently stopped bounded request trace.
    /// </summary>
    Traces
}
