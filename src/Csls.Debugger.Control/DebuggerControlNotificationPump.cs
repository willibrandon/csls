using Csls.Debugger.Contracts;
using StreamJsonRpc;
using System.Threading.Channels;

namespace Csls.Debugger.Control;

/// <summary>
/// Coalesces engine resource invalidations onto the ordered private RPC stream.
/// </summary>
internal sealed class DebuggerControlNotificationPump : IDisposable
{
    private readonly DebuggerControlService _service;
    private readonly JsonRpc _rpc;
    private readonly Channel<bool> _signals =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    private int _pendingKinds;
    private int _completed;

    /// <summary>
    /// Starts observing one debugger control service.
    /// </summary>
    internal DebuggerControlNotificationPump(DebuggerControlService service, JsonRpc rpc)
    {
        _service = service;
        _rpc = rpc;
        _service.ResourceChanged += OnResourceChanged;
    }

    /// <summary>
    /// Sends invalidations until the pump is completed.
    /// </summary>
    internal async Task RunAsync()
    {
        await foreach (bool _ in _signals.Reader.ReadAllAsync()
            .ConfigureAwait(false))
        {
            var change = new DebuggerResourceChangeEventArgs
            {
                Kind = (DebuggerResourceChangeKind)Interlocked.Exchange(
                    ref _pendingKinds,
                    0)
            };
            try
            {
                await _rpc.NotifyAsync(
                    DebuggerControlNotifications.ResourceChanged,
                    change).ConfigureAwait(false);
            }
            catch (ConnectionLostException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Stops observing and lets the notification reader drain accepted invalidations.
    /// </summary>
    internal void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        _service.ResourceChanged -= OnResourceChanged;
        _signals.Writer.TryComplete();
    }

    /// <summary>
    /// Stops observing and completes the accepted-invalidation channel.
    /// </summary>
    public void Dispose() => Complete();

    private void OnResourceChanged(object? sender, DebuggerResourceChangeEventArgs change)
    {
        _ = sender;
        _ = Interlocked.Or(ref _pendingKinds, (int)change.Kind);
        _signals.Writer.TryWrite(true);
    }
}
