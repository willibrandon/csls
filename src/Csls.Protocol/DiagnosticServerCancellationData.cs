namespace Csls.Protocol;

/// <summary>
/// Tells a pull-diagnostic client whether it should repeat a server-cancelled request.
/// </summary>
public sealed record DiagnosticServerCancellationData
{
    /// <summary>
    /// Gets whether the client should request diagnostics again for the current document state.
    /// </summary>
    public bool RetriggerRequest { get; init; }
}
