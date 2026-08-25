using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Csls.Control;

/// <summary>
/// Tracks the lifetime of requests dispatched through one control connection.
/// </summary>
internal sealed class ControlJsonRpc : JsonRpc
{
    private readonly ControlConnectionActivity _activity;

    /// <summary>
    /// Creates one explicitly configured control RPC connection.
    /// </summary>
    /// <param name="messageHandler">The bounded control message handler.</param>
    /// <param name="activity">The connection activity tracker.</param>
    internal ControlJsonRpc(
        IJsonRpcMessageHandler messageHandler,
        ControlConnectionActivity activity)
        : base(messageHandler)
    {
        ArgumentNullException.ThrowIfNull(activity);
        _activity = activity;
    }

    /// <inheritdoc />
    protected override async ValueTask<JsonRpcMessage> DispatchRequestAsync(
        JsonRpcRequest request,
        TargetMethod targetMethod,
        CancellationToken cancellationToken)
    {
        _activity.BeginRequest();
        try
        {
            return await base.DispatchRequestAsync(request, targetMethod, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _activity.EndRequest();
        }
    }
}
