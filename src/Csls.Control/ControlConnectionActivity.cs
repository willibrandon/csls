using System.Diagnostics;

namespace Csls.Control;

/// <summary>
/// Tracks complete-message and request activity for one control connection.
/// </summary>
internal sealed class ControlConnectionActivity
{
    private readonly Lock _gate = new();
    private long _lastActivityTimestamp = Stopwatch.GetTimestamp();
    private int _activeRequestCount;

    /// <summary>
    /// Records receipt of one complete control message.
    /// </summary>
    internal void ObserveMessage()
    {
        lock (_gate)
        {
            _lastActivityTimestamp = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// Records the start of one dispatched control request.
    /// </summary>
    internal void BeginRequest()
    {
        lock (_gate)
        {
            _activeRequestCount = checked(_activeRequestCount + 1);
            _lastActivityTimestamp = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// Records completion of one dispatched control request.
    /// </summary>
    internal void EndRequest()
    {
        lock (_gate)
        {
            if (_activeRequestCount <= 0)
            {
                throw new InvalidOperationException(
                    "A control request completed without a matching start.");
            }

            _activeRequestCount--;
            _lastActivityTimestamp = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// Waits until no request is active and the connection reaches its inactivity limit.
    /// </summary>
    /// <param name="idleTimeout">The maximum permitted inactivity.</param>
    /// <param name="cancellationToken">The server or connection cancellation token.</param>
    /// <returns>A task that completes when the connection is idle.</returns>
    internal async Task WaitUntilIdleAsync(
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);
        while (true)
        {
            TimeSpan delay;
            lock (_gate)
            {
                if (_activeRequestCount > 0)
                {
                    delay = idleTimeout;
                }
                else
                {
                    TimeSpan elapsed = Stopwatch.GetElapsedTime(_lastActivityTimestamp);
                    if (elapsed >= idleTimeout)
                    {
                        return;
                    }

                    delay = idleTimeout - elapsed;
                }
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
