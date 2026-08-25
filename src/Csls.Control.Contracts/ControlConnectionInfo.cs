namespace Csls.Control.Contracts;

/// <summary>
/// Describes the negotiated lifetime settings for one control connection.
/// </summary>
public sealed class ControlConnectionInfo
{
    /// <summary>
    /// Gets the version of the control protocol used by this connection.
    /// </summary>
    public int ProtocolVersion { get; init; }

    /// <summary>
    /// Gets the server inactivity limit in milliseconds.
    /// </summary>
    public int IdleTimeoutMilliseconds { get; init; }

    /// <summary>
    /// Gets the client keepalive interval in milliseconds.
    /// </summary>
    public int KeepAliveIntervalMilliseconds { get; init; }
}
