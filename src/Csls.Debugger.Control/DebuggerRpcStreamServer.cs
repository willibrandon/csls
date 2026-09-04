using Csls.Control;
using Csls.Debugger.Contracts;
using StreamJsonRpc;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Control;

/// <summary>
/// Hosts one debugger control connection over private caller-owned streams.
/// </summary>
public static class DebuggerRpcStreamServer
{
    private const int MaximumMessageBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Serves debugger RPC until the receiving stream closes or cancellation is requested.
    /// </summary>
    /// <param name="receivingStream">The readable stream carrying requests.</param>
    /// <param name="sendingStream">The writable stream carrying responses.</param>
    /// <param name="target">The debugger control implementation.</param>
    /// <param name="cancellationToken">Cancels the private connection.</param>
    /// <returns>A task that completes after the connection drains.</returns>
    public static async Task RunAsync(
        Stream receivingStream,
        Stream sendingStream,
        IDebuggerControlTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receivingStream);
        ArgumentNullException.ThrowIfNull(sendingStream);
        ArgumentNullException.ThrowIfNull(target);
        if (!receivingStream.CanRead)
        {
            throw new ArgumentException(
                "The debugger RPC receiving stream must be readable.",
                nameof(receivingStream));
        }

        if (!sendingStream.CanWrite)
        {
            throw new ArgumentException(
                "The debugger RPC sending stream must be writable.",
                nameof(sendingStream));
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
        using NerdbankMessagePackFormatter formatter = DebuggerControlRpcFormatter.Create();
        using var handler = new LengthHeaderMessageHandler(
            boundedSendingStream,
            boundedReceivingStream,
            formatter);
        using var rpc = new JsonRpc(handler)
        {
            CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
            DisplayName = "debugger-control-server",
            SynchronizationContext = null
        };
        DebuggerControlMethodRegistry.Register(rpc, target);
        using DebuggerControlNotificationPump? notifications =
            target is DebuggerControlService service
            ? new DebuggerControlNotificationPump(service, rpc)
            : null;
        Task? notificationTask = notifications?.RunAsync();
        rpc.StartListening();
        try
        {
            Task rpcCompletion = rpc.Completion.WaitAsync(cancellationToken);
            if (notificationTask is not null &&
                await Task.WhenAny(rpcCompletion, notificationTask).ConfigureAwait(false) ==
                    notificationTask)
            {
                await notificationTask.ConfigureAwait(false);
            }

            await rpcCompletion.ConfigureAwait(false);
            await rpc.DispatchCompletion.ConfigureAwait(false);
        }
        finally
        {
            notifications?.Complete();
            if (notificationTask is not null)
            {
                await notificationTask.ConfigureAwait(
                    ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }
}
