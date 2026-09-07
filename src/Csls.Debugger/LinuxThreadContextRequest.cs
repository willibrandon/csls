using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace Csls.Debugger;

/// <summary>
/// Completes one synchronous native context capture on the Linux child-wait owner.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxThreadContextRequest(int threadId, CancellationToken cancellationToken)
{
    private readonly object _gate = new();
    private byte[]? _registers;
    private ExceptionDispatchInfo? _failure;
    private bool _completed;

    /// <summary>
    /// Waits for the synchronous native transaction and propagates its original failure.
    /// </summary>
    /// <returns>The captured register image after the inspector has detached.</returns>
    internal byte[] WaitForCompletion()
    {
        lock (_gate)
        {
            while (!_completed)
            {
                Monitor.Wait(_gate);
            }

            if (_failure is { } failure)
            {
                failure.Throw();
            }

            return _registers ?? throw new InvalidOperationException("Native inspection completed without registers.");
        }
    }

    /// <summary>
    /// Executes the complete trace acquisition and release on the current native thread.
    /// </summary>
    internal void Execute()
    {
        try
        {
            Complete(LinuxThreadContext.ReadRegisters(threadId, cancellationToken), null);
        }
        catch (OperationCanceledException exception)
        {
            Complete(null, exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Complete(null, exception);
        }
    }

    /// <summary>
    /// Completes an unstarted capture when the owning child has exited.
    /// </summary>
    internal void TargetExited() => Complete(null,
        new InvalidOperationException("The target exited before native register inspection could begin."));

    private void Complete(byte[]? registers, Exception? failure)
    {
        lock (_gate)
        {
            _registers = registers;
            _failure = failure is null ? null : ExceptionDispatchInfo.Capture(failure);
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }
}
