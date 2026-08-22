namespace Csls.Protocol;

/// <summary>
/// Advertises the server's pull-diagnostic behavior to an LSP client.
/// </summary>
public sealed record DiagnosticOptions
{
    /// <summary>
    /// Gets the stable provider identifier used for diagnostic result tracking.
    /// </summary>
    public string? Identifier { get; init; }

    /// <summary>
    /// Gets whether one document can affect diagnostics in another document.
    /// </summary>
    public bool InterFileDependencies { get; init; }

    /// <summary>
    /// Gets whether workspace-wide diagnostic requests are supported.
    /// </summary>
    public bool WorkspaceDiagnostics { get; init; }
}
