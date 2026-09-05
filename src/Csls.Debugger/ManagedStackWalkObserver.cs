using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Reports actor-owned stack progress while retaining inspection and notification failures.
/// </summary>
/// <param name="progress">The optional request-scoped progress receiver.</param>
internal sealed class ManagedStackWalkObserver(IProgress<DebugStackWalkProgress>? progress)
{
    private IProgress<DebugStackWalkProgress>? _progress = progress;

    /// <summary>
    /// Delivers progress synchronously and retires a receiver that throws.
    /// </summary>
    /// <param name="value">The current traversal and reference ownership snapshot.</param>
    internal void Report(DebugStackWalkProgress value)
    {
        try
        {
            _progress?.Report(value);
        }
        catch (Exception failure) when (failure is IOException or InvalidOperationException or OperationCanceledException)
        {
            _progress = null;
            throw new InvalidOperationException("The stack progress receiver failed.", failure);
        }
        catch
        {
            _progress = null;
            throw;
        }
    }

    /// <summary>
    /// Reports cleanup and retains both exceptions for recoverable notification failures.
    /// </summary>
    /// <param name="value">The final snapshot after native cleanup.</param>
    /// <param name="failure">The original exception included if notification also fails.</param>
    internal void ReportFailure(DebugStackWalkProgress value, Exception failure)
    {
        try
        {
            Report(value);
        }
        catch (InvalidOperationException notificationFailure)
        {
            throw new AggregateException("Stack inspection and its failure notification both failed.", failure, notificationFailure);
        }
    }
}
