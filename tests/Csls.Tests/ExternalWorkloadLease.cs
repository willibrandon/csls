namespace Csls.Tests;

/// <summary>
/// Bounds concurrent real-process test workloads according to available processors and memory.
/// </summary>
internal sealed class ExternalWorkloadLease : IDisposable
{
    private const int LogicalProcessorsPerWorkload = 4;
    private const long BytesPerWorkload = 2L * 1024 * 1024 * 1024;
    private static readonly AsyncLocal<ExternalWorkloadLease?> s_current = new();
    private static readonly SemaphoreSlim s_capacity = new(CalculateCapacity());
    private int _referenceCount = 1;

    private ExternalWorkloadLease()
    {
    }

    /// <summary>
    /// Acquires a lease for a real external workload, reusing the current test's lease when present.
    /// </summary>
    /// <returns>The acquired workload lease.</returns>
    internal static ExternalWorkloadLease Acquire()
    {
        ExternalWorkloadLease? current = TryAddReference();
        if (current is not null)
        {
            return current;
        }

        s_capacity.Wait();
        var lease = new ExternalWorkloadLease();
        s_current.Value = lease;
        return lease;
    }

    /// <summary>
    /// Asynchronously acquires a lease for a real external workload.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The acquired workload lease.</returns>
    internal static ValueTask<ExternalWorkloadLease> AcquireAsync(
        CancellationToken cancellationToken)
    {
        ExternalWorkloadLease? current = TryAddReference();
        return current is not null
            ? ValueTask.FromResult(current)
            : WaitForCapacityAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Decrement(ref _referenceCount) != 0)
        {
            return;
        }

        if (ReferenceEquals(s_current.Value, this))
        {
            s_current.Value = null;
        }

        s_capacity.Release();
    }

    /// <summary>
    /// Releases a lease owned by a longer-lived test fixture.
    /// </summary>
    internal void Release() => Dispose();

    private static int CalculateCapacity()
    {
        int processorCapacity = Math.Max(
            1,
            Environment.ProcessorCount / LogicalProcessorsPerWorkload);
        long availableMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        int memoryCapacity = availableMemory > 0
            ? Math.Max(1, (int)Math.Min(int.MaxValue, availableMemory / BytesPerWorkload))
            : processorCapacity;
        return Math.Min(processorCapacity, memoryCapacity);
    }

    private static ExternalWorkloadLease? TryAddReference()
    {
        ExternalWorkloadLease? current = s_current.Value;
        if (current is null)
        {
            return null;
        }

        int referenceCount = Volatile.Read(ref current._referenceCount);
        while (referenceCount != 0)
        {
            int observed = Interlocked.CompareExchange(
                ref current._referenceCount,
                referenceCount + 1,
                referenceCount);
            if (observed == referenceCount)
            {
                return current;
            }

            referenceCount = observed;
        }

        return null;
    }

    private static async ValueTask<ExternalWorkloadLease> WaitForCapacityAsync(
        CancellationToken cancellationToken)
    {
        await s_capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
        var lease = new ExternalWorkloadLease();
        s_current.Value = lease;
        return lease;
    }
}
