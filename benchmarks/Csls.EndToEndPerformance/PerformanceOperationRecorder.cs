using System.Diagnostics;

namespace Csls.EndToEndPerformance;

/// <summary>
/// Records named operation durations in their execution order.
/// </summary>
internal sealed class PerformanceOperationRecorder
{
    private readonly List<PerformanceOperation> _operations = [];

    /// <summary>
    /// Gets the completed operations in execution order.
    /// </summary>
    internal IReadOnlyList<PerformanceOperation> Operations => _operations;

    /// <summary>
    /// Measures an asynchronous operation without a result.
    /// </summary>
    /// <param name="name">The stable operation name.</param>
    /// <param name="operation">The real operation to execute.</param>
    /// <returns>A task that completes with the operation.</returns>
    internal async Task MeasureAsync(string name, Func<Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(operation);
        long startedTimestamp = Stopwatch.GetTimestamp();
        await operation().ConfigureAwait(false);
        Add(name, Stopwatch.GetElapsedTime(startedTimestamp));
    }

    /// <summary>
    /// Measures an asynchronous operation that returns a result.
    /// </summary>
    /// <typeparam name="T">The operation result type.</typeparam>
    /// <param name="name">The stable operation name.</param>
    /// <param name="operation">The real operation to execute.</param>
    /// <returns>The operation result.</returns>
    internal async Task<T> MeasureAsync<T>(string name, Func<Task<T>> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(operation);
        long startedTimestamp = Stopwatch.GetTimestamp();
        T result = await operation().ConfigureAwait(false);
        Add(name, Stopwatch.GetElapsedTime(startedTimestamp));
        return result;
    }

    private void Add(string name, TimeSpan elapsed)
    {
        _operations.Add(new PerformanceOperation
        {
            Name = name,
            Milliseconds = elapsed.TotalMilliseconds
        });
    }
}
