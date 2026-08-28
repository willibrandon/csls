using Csls.Protocol;
using StreamJsonRpc;

namespace Csls.Rpc;

/// <summary>
/// Hosts an explicitly registered StreamJsonRpc LSP connection over two streams.
/// </summary>
public static class LspRpcServer
{
    private const int MaximumHeaderBytes = 8 * 1024;
    private const int MaximumPayloadBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Runs an LSP connection until EOF, disconnection, failure, or cancellation.
    /// </summary>
    /// <param name="input">The protocol-only client-to-server stream.</param>
    /// <param name="output">The protocol-only server-to-client stream.</param>
    /// <param name="target">The language server method target.</param>
    /// <param name="client">The bidirectional connection used for server-to-client requests.</param>
    /// <param name="cancellationToken">The server cancellation token.</param>
    /// <returns>A task that completes after dispatched methods have retired.</returns>
    public static async Task RunAsync(
        Stream input,
        Stream output,
        ILspRpcTarget target,
        LspClientConnection client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(client);

        using var formatter = new LspJsonRpcFormatter(LspRpcJson.CreateSerializerOptions());
        using var boundedInput = new BoundedLspInputStream(
            input,
            MaximumHeaderBytes,
            MaximumPayloadBytes,
            leaveOpen: true);
        using var messageHandler = new HeaderDelimitedMessageHandler(
            output,
            boundedInput,
            formatter);
        await RunAsync(messageHandler, target, client, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs an LSP connection over a complete-message transport until it disconnects.
    /// </summary>
    /// <param name="messageHandler">The bidirectional complete-message transport.</param>
    /// <param name="target">The language server method target.</param>
    /// <param name="client">The bidirectional connection used for server-to-client requests.</param>
    /// <param name="cancellationToken">The server cancellation token.</param>
    /// <returns>A task that completes after dispatched methods have retired.</returns>
    public static async Task RunAsync(
        IJsonRpcMessageHandler messageHandler,
        ILspRpcTarget target,
        LspClientConnection client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageHandler);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(client);

        using var rpc = new JsonRpc(messageHandler)
        {
            CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
            DisplayName = "csls-lsp"
        };
        client.Bind(rpc);
        try
        {
            LspMethodRegistry.Register(rpc, target);
            rpc.StartListening();

            ValueTask completion = new(rpc.Completion.WaitAsync(cancellationToken));
            await completion.ConfigureAwait(false);
            ValueTask dispatchCompletion = new(rpc.DispatchCompletion);
            await dispatchCompletion.ConfigureAwait(false);
        }
        finally
        {
            client.Unbind(rpc);
        }
    }
}
