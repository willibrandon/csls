namespace Csls.Tests;

/// <summary>
/// Bounds concurrent real-process test workloads according to available processors and memory.
/// </summary>
internal sealed class ExternalWorkloadLease : IDisposable
{
    private const int LogicalProcessorsPerWorkload = 16;
    private const long BytesPerWorkload = 8L * 1024 * 1024 * 1024;
    private static readonly AsyncLocal<ExternalWorkloadLease?> s_current = new();
    private static readonly int s_capacityCount = CalculateCapacity();
    private static readonly SemaphoreSlim s_capacity = new(s_capacityCount);
    private readonly CancellationTokenSource _admissionSource;
    private readonly Task _admissionTask;
    private int _admitted;
    private int _admissionCompleted;
    private int _admissionSourceDisposed;
    private int _capacityReleased;
    private int _referenceCount = 1;

    /// <summary>
    /// Gets the number of independent external workloads admitted by this test host.
    /// </summary>
    internal static int Capacity => s_capacityCount;

    private ExternalWorkloadLease(CancellationToken cancellationToken)
    {
        _admissionSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _admissionTask = WaitForAdmissionAsync(_admissionSource.Token);
    }

    /// <summary>
    /// Asynchronously acquires a lease without blocking a test-host ThreadPool thread.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The acquired workload lease.</returns>
    internal static ValueTask<ExternalWorkloadLease> AcquireAsync(
        CancellationToken cancellationToken)
    {
        ExternalWorkloadLease? current = TryAddReference();
        if (current is not null)
        {
            return current.WaitForAdmissionAsync();
        }

        var lease = new ExternalWorkloadLease(cancellationToken);
        s_current.Value = lease;
        return lease.WaitForAdmissionAsync();
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

        _admissionSource.Cancel();
        TryReleaseCapacity();
        TryDisposeAdmissionSource();
    }

    /// <summary>
    /// Releases a lease owned by a longer-lived test fixture.
    /// </summary>
    internal void Release() => Dispose();

    private static int CalculateCapacity() => CalculateCapacity(
        Environment.ProcessorCount,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    /// <summary>
    /// Calculates the number of concurrent real external workloads supported by fixed resources.
    /// </summary>
    internal static int CalculateCapacity(
        int logicalProcessorCount,
        long availableMemoryBytes)
    {
        int processorCapacity = Math.Max(
            1,
            logicalProcessorCount / LogicalProcessorsPerWorkload);
        int memoryCapacity = availableMemoryBytes > 0
            ? Math.Max(1, (int)Math.Min(int.MaxValue, availableMemoryBytes / BytesPerWorkload))
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

    private async Task WaitForAdmissionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await s_capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _admitted, 1);
            TryReleaseCapacity();
        }
        finally
        {
            Volatile.Write(ref _admissionCompleted, 1);
            TryDisposeAdmissionSource();
        }
    }

    private async ValueTask<ExternalWorkloadLease> WaitForAdmissionAsync()
    {
        try
        {
            await _admissionTask
                .WaitAsync(CancellationToken.None)
                .ConfigureAwait(false);
            return this;
        }
        catch
        {
            Release();
            throw;
        }
    }

    private void TryReleaseCapacity()
    {
        if (Volatile.Read(ref _admitted) != 0 &&
            Volatile.Read(ref _referenceCount) == 0 &&
            Interlocked.Exchange(ref _capacityReleased, 1) == 0)
        {
            s_capacity.Release();
        }
    }

    private void TryDisposeAdmissionSource()
    {
        if (Volatile.Read(ref _admissionCompleted) != 0 &&
            Volatile.Read(ref _referenceCount) == 0 &&
            Interlocked.Exchange(ref _admissionSourceDisposed, 1) == 0)
        {
            _admissionSource.Dispose();
        }
    }
}
