using System.Threading.Channels;

namespace Csls.Debugger;

/// <summary>
/// Serializes debugger commands and runtime callbacks through one bounded queue.
/// </summary>
internal sealed class DebuggerSessionActor : IAsyncDisposable
{
    private const int Capacity = 64;
    private readonly Channel<DebuggerSessionWorkItem> _channel;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _runner;
    private int _disposed;

    /// <summary>
    /// Creates and starts a debugger session actor.
    /// </summary>
    internal DebuggerSessionActor()
    {
        _channel = Channel.CreateBounded<DebuggerSessionWorkItem>(
            new BoundedChannelOptions(Capacity)
            {
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        _runner = RunAsync();
    }

    /// <summary>
    /// Queues one operation and waits for its serialized completion.
    /// </summary>
    /// <param name="operation">The operation to run on the actor.</param>
    /// <param name="cancellationToken">Cancels queueing or operation execution.</param>
    /// <returns>A task that completes with the operation.</returns>
    internal async Task InvokeAsync(
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(operation);
        var workItem = new DebuggerSessionWorkItem(operation, cancellationToken);
        await _channel.Writer.WriteAsync(workItem, cancellationToken).ConfigureAwait(false);
        await workItem.Completion.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _channel.Writer.TryComplete();
        await _runner.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (DebuggerSessionWorkItem workItem in _channel.Reader.ReadAllAsync(
                _lifetime.Token).ConfigureAwait(false))
            {
                await workItem.ExecuteAsync(_lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }
}
