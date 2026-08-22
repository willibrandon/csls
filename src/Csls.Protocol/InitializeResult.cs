namespace Csls.Protocol;

/// <summary>
/// Returns negotiated server capabilities and implementation information.
/// </summary>
public sealed record InitializeResult
{
    /// <summary>
    /// Gets the capabilities supported by the server.
    /// </summary>
    public required ServerCapabilities Capabilities { get; init; }

    /// <summary>
    /// Gets information about the server implementation.
    /// </summary>
    public required ServerInfo ServerInfo { get; init; }
}
