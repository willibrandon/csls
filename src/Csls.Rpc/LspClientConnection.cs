using Csls.Protocol;
using StreamJsonRpc;
using System.Text.Json;

namespace Csls.Rpc;

/// <summary>
/// Sends explicitly supported LSP requests from the server to its connected client.
/// </summary>
public sealed class LspClientConnection : ILspClientConnection
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
    /// Creates one server-owned work-done progress token in the connected LSP client.
    /// </summary>
    /// <param name="parameters">The unique server-generated progress token.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A task that completes after the client accepts the token.</returns>
    public async Task CreateWorkDoneProgressAsync(
        WorkDoneProgressCreateParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        JsonRpc rpc = Volatile.Read(ref _rpc)
            ?? throw new InvalidOperationException("The LSP client is not connected.");
        await rpc.InvokeWithParameterObjectAsync<object?>(
            "window/workDoneProgress/create",
            parameters,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes one begin, report, or end value for a server-owned progress token.
    /// </summary>
    /// <param name="parameters">The token and typed work-done progress value.</param>
    /// <returns>A task that completes after the notification is written.</returns>
    public Task PublishWorkDoneProgressAsync(WorkDoneProgressParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        JsonRpc rpc = Volatile.Read(ref _rpc)
            ?? throw new InvalidOperationException("The LSP client is not connected.");
        return rpc.NotifyWithParameterObjectAsync("$/progress", parameters);
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
    /// Registers server-requested capabilities with the connected LSP client.
    /// </summary>
    /// <param name="parameters">The ordered capability registrations.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A task that completes after the client accepts the registrations.</returns>
    public async Task RegisterCapabilityAsync(
        RegistrationParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        JsonRpc rpc = Volatile.Read(ref _rpc)
            ?? throw new InvalidOperationException("The LSP client is not connected.");
        try
        {
            await rpc.InvokeWithParameterObjectAsync<object?>(
                "client/registerCapability",
                parameters,
                cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteRpcException exception)
        {
            throw new InvalidOperationException(
                "The LSP client rejected dynamic capability registration.",
                exception);
        }
    }

    /// <summary>
    /// Requests that the connected LSP client refresh pull diagnostics.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A task that completes after the client accepts the refresh.</returns>
    public async Task RefreshDiagnosticsAsync(CancellationToken cancellationToken)
    {
        JsonRpc rpc = Volatile.Read(ref _rpc)
            ?? throw new InvalidOperationException("The LSP client is not connected.");
        await rpc.InvokeWithParameterObjectAsync<object?>(
            "workspace/diagnostic/refresh",
            argument: (object?)null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Requests that the connected LSP client refresh inlay hints.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A task that completes after the client accepts the refresh.</returns>
    public async Task RefreshInlayHintsAsync(CancellationToken cancellationToken)
    {
        JsonRpc rpc = Volatile.Read(ref _rpc)
            ?? throw new InvalidOperationException("The LSP client is not connected.");
        await rpc.InvokeWithParameterObjectAsync<object?>(
            "workspace/inlayHint/refresh",
            argument: (object?)null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Requests that the connected LSP client refresh code lenses.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A task that completes after the client accepts the refresh.</returns>
    public async Task RefreshCodeLensesAsync(CancellationToken cancellationToken)
    {
        JsonRpc rpc = Volatile.Read(ref _rpc)
            ?? throw new InvalidOperationException("The LSP client is not connected.");
        try
        {
            await rpc.InvokeWithParameterObjectAsync<object?>(
                "workspace/codeLens/refresh",
                argument: (object?)null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteRpcException exception)
        {
            throw new InvalidOperationException(
                "The LSP client rejected a CodeLens refresh request.",
                exception);
        }
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
