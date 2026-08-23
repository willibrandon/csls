using BenchmarkDotNet.Attributes;
using Csls.Core;

namespace Csls.Benchmarks;

/// <summary>
/// Measures request admission and retirement through the bounded scheduler.
/// </summary>
[BenchmarkCategory("Scheduling")]
[MemoryDiagnoser]
public class RequestSchedulerBenchmarks : IAsyncDisposable
{
    private Func<RequestContext, ValueTask<long>> _operation = null!;
    private RequestScheduler _scheduler = null!;
    private Func<long> _workspaceGenerationProvider = null!;

    /// <summary>
    /// Gets or sets whether bounded request tracing is active during measurement.
    /// </summary>
    [Params(false, true)]
    public bool TraceEnabled { get; set; }

    /// <summary>
    /// Creates a single-lane scheduler and reusable delegates before measurement.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _scheduler = new RequestScheduler(
            capacity: 64,
            foregroundConcurrency: 1,
            backgroundConcurrency: 1);
        _workspaceGenerationProvider = static () => 1;
        _operation = static context => ValueTask.FromResult(context.Ordinal);
        if (TraceEnabled)
        {
            _scheduler.StartTrace();
        }
    }

    /// <summary>
    /// Disposes the scheduler after all measurements for the benchmark case.
    /// </summary>
    [GlobalCleanup]
    public async ValueTask DisposeAsync()
    {
        if (TraceEnabled)
        {
            _scheduler.StopTrace();
        }

        await _scheduler.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Measures one foreground read request through the scheduler.
    /// </summary>
    [Benchmark(Baseline = true)]
    public Task<long> ScheduleReadAsync() => ScheduleAsync(RequestMode.ReadOnly);

    /// <summary>
    /// Measures one exclusive mutation request through the scheduler.
    /// </summary>
    [Benchmark]
    public Task<long> ScheduleMutationAsync() => ScheduleAsync(RequestMode.ReadWrite);

    /// <summary>
    /// Measures one background read request through the scheduler.
    /// </summary>
    [Benchmark]
    public Task<long> ScheduleBackgroundReadAsync() =>
        ScheduleAsync(RequestMode.ReadOnlyBackground);

    private Task<long> ScheduleAsync(RequestMode mode) =>
        _scheduler.ScheduleAsync(
            "benchmark/request",
            mode,
            _workspaceGenerationProvider,
            _operation,
            CancellationToken.None);
}
