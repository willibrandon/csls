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
            await _rpc.NotifyAsync(
                DebuggerControlNotifications.ResourceChanged,
                change).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops observing and drains already accepted invalidations.
    /// </summary>
    public void Dispose()
    {
        _service.ResourceChanged -= OnResourceChanged;
        _signals.Writer.TryComplete();
    }

    private void OnResourceChanged(object? sender, DebuggerResourceChangeEventArgs change)
    {
        _ = sender;
        _ = Interlocked.Or(ref _pendingKinds, (int)change.Kind);
        _signals.Writer.TryWrite(true);
    }
}
