using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Csls.Control;

/// <summary>
/// Observes complete inbound messages on the bounded control transport.
/// </summary>
internal sealed class ControlMessageHandler : LengthHeaderMessageHandler
{
    private readonly ControlConnectionActivity _activity;

    /// <summary>
    /// Creates a length-prefixed handler for one control connection.
    /// </summary>
    /// <param name="sendingStream">The bounded sending stream.</param>
    /// <param name="receivingStream">The bounded receiving stream.</param>
    /// <param name="formatter">The source-generated JSON formatter.</param>
    /// <param name="activity">The connection activity tracker.</param>
    internal ControlMessageHandler(
        Stream sendingStream,
        Stream receivingStream,
        IJsonRpcMessageFormatter formatter,
        ControlConnectionActivity activity)
        : base(sendingStream, receivingStream, formatter)
    {
        ArgumentNullException.ThrowIfNull(activity);
        _activity = activity;
    }

    /// <inheritdoc />
    protected override async ValueTask<JsonRpcMessage?> ReadCoreAsync(
        CancellationToken cancellationToken)
    {
        JsonRpcMessage? message = await base.ReadCoreAsync(cancellationToken)
            .ConfigureAwait(false);
        if (message is not null)
        {
            _activity.ObserveMessage();
        }

        return message;
    }
}
