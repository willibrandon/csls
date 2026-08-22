using System.Text.Json;

namespace Csls.Protocol;

/// <summary>
/// Contains the client's initialization request and negotiated capabilities.
/// </summary>
public sealed record InitializeParams
{
    /// <summary>
    /// Gets the client process identifier when one is available.
    /// </summary>
    public int? ProcessId { get; init; }

    /// <summary>
    /// Gets information about the connected client.
    /// </summary>
    public ClientInfo? ClientInfo { get; init; }

    /// <summary>
    /// Gets the client's locale.
    /// </summary>
    public string? Locale { get; init; }

    /// <summary>
    /// Gets the legacy root path.
    /// </summary>
    public string? RootPath { get; init; }

    /// <summary>
    /// Gets the legacy root URI.
    /// </summary>
    public DocumentUri? RootUri { get; init; }

    /// <summary>
    /// Gets client-specific initialization options.
    /// </summary>
    public JsonElement? InitializationOptions { get; init; }

    /// <summary>
    /// Gets the complete client capability object.
    /// </summary>
    public JsonElement Capabilities { get; init; }

    /// <summary>
    /// Gets the requested protocol trace level.
    /// </summary>
    public string? Trace { get; init; }

    /// <summary>
    /// Gets the client's workspace folders.
    /// </summary>
    public IReadOnlyList<WorkspaceFolder>? WorkspaceFolders { get; init; }
}
