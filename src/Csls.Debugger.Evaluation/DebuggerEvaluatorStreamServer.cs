using Csls.Control;
using Csls.Debugger.Contracts;
using StreamJsonRpc;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Evaluation;

/// <summary>
/// Hosts one bounded evaluator RPC connection over caller-owned streams.
/// </summary>
public static class DebuggerEvaluatorStreamServer
{
    private const int MaximumMessageBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Serves evaluator requests until input closes or cancellation is requested.
    /// </summary>
    /// <param name="receivingStream">The protocol-only request stream.</param>
    /// <param name="sendingStream">The protocol-only response stream.</param>
    /// <param name="target">The compiler-backed evaluator target.</param>
    /// <param name="cancellationToken">Cancels the evaluator connection.</param>
    /// <returns>A task that completes after request dispatch drains.</returns>
    public static async Task RunAsync(
        Stream receivingStream,
        Stream sendingStream,
        IDebuggerEvaluatorTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receivingStream);
        ArgumentNullException.ThrowIfNull(sendingStream);
        ArgumentNullException.ThrowIfNull(target);
        if (!receivingStream.CanRead || !sendingStream.CanWrite)
        {
            throw new ArgumentException(
                "Evaluator RPC requires a readable request stream and writable response stream.");
        }

        var boundedReceivingStream = new BoundedMessageStream(
            receivingStream,
            MaximumMessageBytes,
            leaveOpen: true);
        await using ConfiguredAsyncDisposable receivingCleanup =
            boundedReceivingStream.ConfigureAwait(false);
        var boundedSendingStream = new BoundedMessageStream(
            sendingStream,
            MaximumMessageBytes,
            leaveOpen: true);
        await using ConfiguredAsyncDisposable sendingCleanup =
            boundedSendingStream.ConfigureAwait(false);
        using SystemTextJsonFormatter formatter = DebuggerEvaluatorJson.CreateFormatter();
        using var handler = new LengthHeaderMessageHandler(
            boundedSendingStream,
            boundedReceivingStream,
            formatter);
        using var rpc = new JsonRpc(handler)
        {
            CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
            DisplayName = "debugger-evaluator-server",
            SynchronizationContext = null
        };
        DebuggerEvaluatorMethodRegistry.Register(rpc, target);
        rpc.StartListening();
        await rpc.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        await rpc.DispatchCompletion.ConfigureAwait(false);
    }
}
