using Csls.Protocol;
using StreamJsonRpc;
using System.Text.Json;

namespace Csls.Rpc;

/// <summary>
/// Sends explicitly supported LSP requests from the server to its connected client.
/// </summary>
public sealed class LspClientConnection
{
    private JsonRpc? _rpc;

    /// <summary>
    /// Requests ordered configuration sections from the connected LSP client.
    /// </summary>
    /// <param name="parameters">The ordered configuration section requests.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The client values in the same order as the requested sections.</returns>
    public Task<JsonElement?[]> GetConfigurationAsync(
        ConfigurationParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        JsonRpc rpc = Volatile.Read(ref _rpc)
            ?? throw new InvalidOperationException("The LSP client is not connected.");
        return rpc.InvokeWithParameterObjectAsync<JsonElement?[]>(
            "workspace/configuration",
            parameters,
            cancellationToken);
    }

    /// <summary>
    /// Publishes one bounded workspace diagnostic batch through the client's partial result token.
    /// </summary>
    /// <param name="parameters">The client token and next diagnostic batch.</param>
    /// <returns>A task that completes after the notification is written.</returns>
    public Task PublishWorkspaceDiagnosticProgressAsync(
        WorkspaceDiagnosticProgressParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        JsonRpc rpc = Volatile.Read(ref _rpc)
            ?? throw new InvalidOperationException("The LSP client is not connected.");
        return rpc.NotifyWithParameterObjectAsync("$/progress", parameters);
    }

    /// <summary>
    /// Replaces the connected client's diagnostic state for one document.
    /// </summary>
    /// <param name="parameters">The document version and complete diagnostic collection.</param>
    /// <returns>A task that completes after the notification is written.</returns>
    public Task PublishDiagnosticsAsync(PublishDiagnosticsParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        JsonRpc rpc = Volatile.Read(ref _rpc)
            ?? throw new InvalidOperationException("The LSP client is not connected.");
        return rpc.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics", parameters);
    }

    /// <summary>
    /// Binds one live StreamJsonRpc session before message dispatch begins.
    /// </summary>
    /// <param name="rpc">The live LSP connection.</param>
    internal void Bind(JsonRpc rpc)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        if (Interlocked.CompareExchange(ref _rpc, rpc, comparand: null) is not null)
        {
            throw new InvalidOperationException("An LSP client is already connected.");
        }
    }

    /// <summary>
    /// Removes the matching StreamJsonRpc session after message dispatch ends.
    /// </summary>
    /// <param name="rpc">The completed LSP connection.</param>
    internal void Unbind(JsonRpc rpc)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _rpc, null, rpc), rpc))
        {
            throw new InvalidOperationException("The completed LSP client is not connected.");
        }
    }
}
