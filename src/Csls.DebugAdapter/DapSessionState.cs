namespace Csls.DebugAdapter;

/// <summary>
/// Identifies the negotiation and lifecycle state of one DAP connection.
/// </summary>
internal enum DapSessionState
{
    /// <summary>
    /// The client has not initialized the connection.
    /// </summary>
    Created,

    /// <summary>
    /// The client initialized the connection.
    /// </summary>
    Initialized,

    /// <summary>
    /// The client may configure the prepared target.
    /// </summary>
    Configuring,

    /// <summary>
    /// The adapter is starting the configured target.
    /// </summary>
    Starting,

    /// <summary>
    /// The target is running.
    /// </summary>
    Running,

    /// <summary>
    /// The target is being ended because its owner disconnected.
    /// </summary>
    Terminating,

    /// <summary>
    /// The connection and its owned target ended normally.
    /// </summary>
    Terminated,

    /// <summary>
    /// The connection ended because of an unrecoverable protocol failure.
    /// </summary>
    Faulted
}
