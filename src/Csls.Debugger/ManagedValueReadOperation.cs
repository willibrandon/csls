using Csls.Debugger.Contracts;
using System.Runtime.ExceptionServices;

namespace Csls.Debugger;

/// <summary>
/// Owns cooperative cancellation and bounded progress for a synchronous live value read.
/// </summary>
/// <param name="reference">The requested variable-container handle.</param>
/// <param name="cancellationToken">Cancels inspection between runtime value operations.</param>
/// <param name="progress">The optional synchronous request-scoped progress receiver.</param>
/// <param name="getRetention">Reads actor-owned registry sizes at the observation point.</param>
internal sealed class ManagedValueReadOperation(
    int reference,
    IProgress<DebugValueReadProgress>? progress,
    Func<(int Values, int MemoryReferences)> getRetention,
    CancellationToken cancellationToken)
{
    private IProgress<DebugValueReadProgress>? _progress = progress;
    private ExceptionDispatchInfo? _notificationFailure;
    private int _formattedValues;

    /// <summary>
    /// Gets the cancellation token shared with native frame reacquisition.
    /// </summary>
    internal CancellationToken CancellationToken => cancellationToken;

    /// <summary>
    /// Checks cancellation before starting another runtime value operation.
    /// </summary>
    internal void CheckCancellation()
    {
        if (_notificationFailure is { } failure)
        {
            failure.Throw();
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Records a formatted runtime value and reports every bounded group of values.
    /// </summary>
    internal void ValueFormatted()
    {
        _formattedValues = checked(_formattedValues + 1);
        if (_formattedValues % 256 == 0)
        {
            Report(DebugValueReadState.Reading, 0);
        }
        CheckCancellation();
    }

    /// <summary>
    /// Reports the completed page before transferring its retained values to the caller.
    /// </summary>
    /// <param name="count">The exact number of values in the completed page.</param>
    internal void Complete(int count)
    {
        CheckCancellation();
        Report(DebugValueReadState.Completed, count);
    }

    /// <summary>
    /// Reports registry sizes after rollback while preserving inspection and notification failures.
    /// </summary>
    /// <param name="failure">The original inspection exception.</param>
    internal void ReportFailure(Exception failure)
    {
        try
        {
            Report(failure is OperationCanceledException ? DebugValueReadState.Canceled : DebugValueReadState.Failed, 0);
        }
        catch (InvalidOperationException notificationFailure)
        {
            throw new AggregateException("Value inspection and its failure notification both failed.", failure, notificationFailure);
        }
    }

    private void Report(DebugValueReadState state, int count)
    {
        if (_progress is null)
        {
            return;
        }

        (int values, int memoryReferences) = getRetention();
        try
        {
            _progress.Report(new DebugValueReadProgress(reference, _formattedValues, count, values, memoryReferences, state));
        }
        catch (Exception failure) when (failure is IOException or InvalidOperationException or OperationCanceledException)
        {
            _progress = null;
            var propagated = new InvalidOperationException("The value progress receiver failed.", failure);
            _notificationFailure = ExceptionDispatchInfo.Capture(propagated);
            throw propagated;
        }
        catch (Exception failure)
        {
            _progress = null;
            _notificationFailure = ExceptionDispatchInfo.Capture(failure);
            throw;
        }
    }
}
