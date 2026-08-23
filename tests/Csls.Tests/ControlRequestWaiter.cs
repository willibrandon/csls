using Csls.Control;
using Csls.Control.Contracts;

namespace Csls.Tests;

/// <summary>
/// Waits for observable request concurrency reported by a real control session.
/// </summary>
internal static class ControlRequestWaiter
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Waits until the expected number of named requests are active concurrently.
    /// </summary>
    /// <param name="client">The connected real control client.</param>
    /// <param name="requestName">The exact scheduled request name.</param>
    /// <param name="expectedCount">The minimum concurrent request count.</param>
    /// <param name="timeout">The maximum observation interval.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The matching active request snapshots.</returns>
    internal static async Task<IReadOnlyList<ControlRequestInfo>> WaitForActiveCountAsync(
        ControlRpcClient client,
        string requestName,
        int expectedCount,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var timer = new PeriodicTimer(s_pollInterval);
        int observedCount = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(timeoutSource.Token).ConfigureAwait(false))
            {
                ControlDashboardSnapshot dashboard = await client.GetDashboardSnapshotAsync(
                    new ControlDashboardRequest { IncludeDiagnostics = false },
                    timeoutSource.Token).ConfigureAwait(false);
                ControlRequestInfo[] requests =
                [
                    .. dashboard.Requests.ActiveRequests.Where(request =>
                        string.Equals(request.Name, requestName, StringComparison.Ordinal))
                ];
                observedCount = requests.Length;
                if (observedCount >= expectedCount)
                {
                    return requests;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Observed {observedCount} active {requestName} requests; expected {expectedCount}.");
        }

        throw new InvalidOperationException("The control-request polling loop ended unexpectedly.");
    }
}
