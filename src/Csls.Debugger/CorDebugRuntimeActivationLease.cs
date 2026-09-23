namespace Csls.Debugger;

/// <summary>
/// Retains exclusive runtime activation ownership until temporary resources are released or transferred.
/// </summary>
internal sealed class CorDebugRuntimeActivationLease : IDisposable
{
    private int _owned = 1;

    private CorDebugRuntimeActivationLease()
    {
    }

    /// <summary>
    /// Acquires the process-wide runtime gate before creating temporary activation resources.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for runtime ownership.</param>
    /// <returns>The scoped activation lease.</returns>
    internal static async Task<CorDebugRuntimeActivationLease> AcquireAsync(CancellationToken cancellationToken)
    {
        await CorDebugRuntimeActivationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new CorDebugRuntimeActivationLease();
    }

    /// <summary>
    /// Transfers runtime ownership to the successfully initialized debuggee.
    /// </summary>
    internal void Transfer()
    {
        if (Interlocked.Exchange(ref _owned, 0) == 0)
        {
            throw new InvalidOperationException("Runtime activation ownership has already been transferred or released.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _owned, 0) != 0)
        {
            CorDebugRuntimeActivationGate.Release();
        }
    }
}
