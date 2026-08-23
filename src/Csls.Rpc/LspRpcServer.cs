using StreamJsonRpc;

namespace Csls.Rpc;

/// <summary>
/// Hosts an explicitly registered StreamJsonRpc LSP connection over two streams.
/// </summary>
public static class LspRpcServer
{
    /// <summary>
    /// Runs an LSP connection until EOF, disconnection, failure, or cancellation.
    /// </summary>
    /// <param name="input">The protocol-only client-to-server stream.</param>
    /// <param name="output">The protocol-only server-to-client stream.</param>
    /// <param name="target">The language server method target.</param>
    /// <param name="cancellationToken">The server cancellation token.</param>
    /// <returns>A task that completes after dispatched methods have retired.</returns>
    public static async Task RunAsync(
        Stream input,
        Stream output,
        ILspRpcTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(target);

        using var formatter = new LspJsonRpcFormatter(LspRpcJson.CreateSerializerOptions());
        using var messageHandler = new HeaderDelimitedMessageHandler(output, input, formatter);
        using var rpc = new JsonRpc(messageHandler)
        {
            CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
            DisplayName = "csls-lsp"
        };
        LspMethodRegistry.Register(rpc, target);
        rpc.StartListening();

        ValueTask completion = new(rpc.Completion.WaitAsync(cancellationToken));
        await completion.ConfigureAwait(false);
        ValueTask dispatchCompletion = new(rpc.DispatchCompletion);
        await dispatchCompletion.ConfigureAwait(false);
    }
}
