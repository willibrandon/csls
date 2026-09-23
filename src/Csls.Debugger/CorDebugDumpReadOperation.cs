using Csls.Debugger.Contracts;
using System.Runtime.ExceptionServices;

namespace Csls.Debugger;

/// <summary>
/// Retains request cancellation and observer failures until a native dump operation unwinds.
/// </summary>
/// <param name="cancellationToken">The requesting client's cancellation token.</param>
/// <param name="progress">The optional synchronous inspection observer.</param>
internal sealed class CorDebugDumpReadOperation(CancellationToken cancellationToken, IProgress<DebugDumpReadProgress>? progress)
{
    private IProgress<DebugDumpReadProgress>? _progress = progress;
    private ExceptionDispatchInfo? _observerFailure;
    private long _memoryReads;
    private long _bytesRead;
    private long _contextReads;

    /// <summary>
    /// Gets the cancellation token belonging to this captured inspection request.
    /// </summary>
    internal CancellationToken CancellationToken => cancellationToken;

    /// <summary>
    /// Gets whether CoreCLR can continue reading captured data for this request.
    /// </summary>
    internal bool CanRead => !cancellationToken.IsCancellationRequested && _observerFailure is null;

    /// <summary>
    /// Records a real captured-memory read and delivers bounded progress inside the callback.
    /// </summary>
    /// <param name="bytesRead">The number of captured bytes copied to the native caller.</param>
    internal void RecordMemoryRead(int bytesRead)
    {
        _memoryReads++;
        _bytesRead += bytesRead;
        ReportReading();
    }

    /// <summary>
    /// Records a completed native thread-context request.
    /// </summary>
    internal void RecordContextRead()
    {
        _contextReads++;
        ReportReading();
    }

    /// <summary>
    /// Restores the original observer failure or caller cancellation after leaving native code.
    /// </summary>
    internal void ThrowIfInterrupted()
    {
        if (_observerFailure is { } failure)
        {
            failure.Throw();
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Delivers the final outcome after temporary native references are released.
    /// </summary>
    /// <param name="state">The final inspection outcome.</param>
    internal void ReportTerminal(DebugDumpReadState state)
    {
        Report(state);
        if (_observerFailure is { } failure)
        {
            failure.Throw();
        }
    }

    /// <summary>
    /// Reports failed inspection while preserving both inspection and notification failures.
    /// </summary>
    /// <param name="state">The failed or canceled inspection outcome.</param>
    /// <param name="failure">The original failure after native cleanup.</param>
    internal void ReportFailure(DebugDumpReadState state, Exception failure)
    {
        if (ReferenceEquals(_observerFailure?.SourceException, failure))
        {
            return;
        }

        try
        {
            ReportTerminal(state);
        }
        catch (Exception notificationFailure) when (ReferenceEquals(_observerFailure?.SourceException, notificationFailure))
        {
            throw new AggregateException("Captured-frame inspection and its failure notification both failed.",
                failure, notificationFailure);
        }
    }

    private void ReportReading()
    {
        long reads = _memoryReads + _contextReads;
        if (reads == 1 || reads % 64 == 0)
        {
            Report(DebugDumpReadState.Reading);
        }
    }

    private void Report(DebugDumpReadState state)
    {
        if (_progress is null)
        {
            return;
        }

        try
        {
            _progress.Report(new DebugDumpReadProgress(_memoryReads, _bytesRead, _contextReads, state));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            _progress = null;
            _observerFailure = ExceptionDispatchInfo.Capture(
                new InvalidOperationException("The captured-memory progress receiver failed.", exception));
        }
        catch (Exception exception)
        {
            _progress = null;
            _observerFailure = ExceptionDispatchInfo.Capture(exception);
            // The generated COM boundary returns an HRESULT; the managed caller restores this failure.
            throw;
        }
    }
}
