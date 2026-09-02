namespace Csls.Mcp.Worker;

/// <summary>
/// Summarizes selected workspace health without embedding high-cardinality detail collections.
/// </summary>
internal sealed class McpWorkspaceSummary
{
    /// <summary>
    /// Gets the selected language-server process identifier.
    /// </summary>
    public int ProcessId { get; init; }

    /// <summary>
    /// Gets the selected language-server lifecycle state.
    /// </summary>
    public required string LifecycleState { get; init; }

    /// <summary>
    /// Gets the selected workspace loading phase.
    /// </summary>
    public string? WorkspacePhase { get; init; }

    /// <summary>
    /// Gets the selected workspace generation.
    /// </summary>
    public long WorkspaceGeneration { get; init; }

    /// <summary>
    /// Gets the number of configured workspace roots.
    /// </summary>
    public int WorkspaceRootCount { get; init; }

    /// <summary>
    /// Gets the number of loaded Roslyn workspaces.
    /// </summary>
    public int WorkspaceCount { get; init; }

    /// <summary>
    /// Gets the number of loaded projects.
    /// </summary>
    public int ProjectCount { get; init; }

    /// <summary>
    /// Gets the number of loaded documents.
    /// </summary>
    public int DocumentCount { get; init; }

    /// <summary>
    /// Gets the number of requests accepted since session start.
    /// </summary>
    public long AcceptedRequestCount { get; init; }

    /// <summary>
    /// Gets the number of requests completed since session start.
    /// </summary>
    public long CompletedRequestCount { get; init; }

    /// <summary>
    /// Gets the number of queued and running requests.
    /// </summary>
    public int ActiveRequestCount { get; init; }

    /// <summary>
    /// Gets the number of requests waiting for scheduler admission.
    /// </summary>
    public int QueuedRequestCount { get; init; }

    /// <summary>
    /// Gets whether one workspace mutation is active.
    /// </summary>
    public bool IsMutationActive { get; init; }

    /// <summary>
    /// Gets whether the selected request scheduler is stopping.
    /// </summary>
    public bool IsStopping { get; init; }

    /// <summary>
    /// Gets the number of active build hosts.
    /// </summary>
    public int BuildHostCount { get; init; }

    /// <summary>
    /// Gets the number of reported caches.
    /// </summary>
    public int CacheCount { get; init; }

    /// <summary>
    /// Gets the number of retained worker log entries available in the detailed resource.
    /// </summary>
    public int RetainedLogCount { get; init; }

    /// <summary>
    /// Gets the MCP resource URI for the complete selected workspace snapshot.
    /// </summary>
    public required string DetailsUri { get; init; }
}
