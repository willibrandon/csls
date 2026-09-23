using Csls.Control;
using Csls.Control.Contracts;

namespace Csls.Tests;

/// <summary>
/// Observes real Zed requests through the control transport and retains scheduler evidence on failure.
/// </summary>
internal static class ZedControlObservation
{
    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    /// Waits for Zed's document-open notification to appear in the real workspace snapshot.
    /// </summary>
    internal static async Task<ControlDashboardSnapshot> WaitForOpenDocumentAsync(
        ControlRpcClient control,
        string documentPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        ControlDashboardSnapshot? lastSnapshot = null;
        try
        {
            while (await timer.WaitForNextTickAsync(timeoutSource.Token).ConfigureAwait(false))
            {
                ControlDashboardSnapshot snapshot =
                    await control.GetDashboardSnapshotAsync(
                        new ControlDashboardRequest { IncludeDiagnostics = false },
                        timeoutSource.Token).ConfigureAwait(false);
                lastSnapshot = snapshot;
                if (snapshot.Documents.Any(document =>
                    document.IsOpen && PathComparer.Equals(document.FilePath, documentPath)))
                {
                    return snapshot;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            string openDocuments = string.Join(
                Environment.NewLine,
                lastSnapshot?.Documents
                    .Where(static document => document.IsOpen)
                    .Select(static document => document.FilePath ?? document.Name) ?? []);
            throw new TimeoutException(
                $"Zed did not open {documentPath} through csls. Open documents:" +
                $"{Environment.NewLine}{openDocuments}");
        }

        throw new InvalidOperationException("The open-document polling loop ended unexpectedly.");
    }

    /// <summary>
    /// Requires successful completed requests for the selected language-server operation.
    /// </summary>
    internal static void AssertSucceeded(ControlTraceInfo trace, string requestName)
    {
        ControlTraceEntry[] completedRequests =
        [
            .. trace.Entries.Where(entry => string.Equals(
                entry.Name,
                requestName,
                StringComparison.Ordinal) &&
                entry.CompletedAt.HasValue)
        ];
        Assert.IsNotEmpty(
            completedRequests,
            $"Zed did not complete {requestName} through csls.");
        foreach (ControlTraceEntry request in completedRequests)
        {
            Assert.AreEqual("Succeeded", request.Status);
            Assert.IsNull(request.ExceptionType);
        }
    }

    /// <summary>
    /// Waits for the actual language-server request to complete within the caller's deadline.
    /// </summary>
    internal static async Task<ControlTraceInfo> WaitForEntryAsync(
        ControlRpcClient control,
        string requestName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        ControlDashboardSnapshot? lastSnapshot = null;
        try
        {
            while (await timer.WaitForNextTickAsync(timeoutSource.Token).ConfigureAwait(false))
            {
                lastSnapshot = await control.GetDashboardSnapshotAsync(
                    new ControlDashboardRequest { IncludeDiagnostics = false },
                    timeoutSource.Token).ConfigureAwait(false);
                if (lastSnapshot.Requests.Trace.Entries.Any(entry =>
                    string.Equals(entry.Name, requestName, StringComparison.Ordinal) &&
                    entry.CompletedAt.HasValue))
                {
                    return lastSnapshot.Requests.Trace;
                }
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Zed did not complete {requestName} through csls.{Environment.NewLine}{Describe(lastSnapshot)}",
                exception);
        }

        throw new InvalidOperationException("The trace polling loop ended unexpectedly.");
    }

    /// <summary>
    /// Observes settlement of the selected operations while preserving request failures for assertion.
    /// </summary>
    internal static async Task WaitForSettledEntriesAsync(
        ControlRpcClient control,
        HashSet<string> requestNames,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        int settledSnapshots = 0;
        ControlDashboardSnapshot? lastSnapshot = null;
        try
        {
            while (await timer.WaitForNextTickAsync(timeoutSource.Token).ConfigureAwait(false))
            {
                lastSnapshot = await control.GetDashboardSnapshotAsync(
                    new ControlDashboardRequest { IncludeDiagnostics = false },
                    timeoutSource.Token).ConfigureAwait(false);
                bool hasRunningRequest = lastSnapshot.Requests.Trace.Entries.Any(entry =>
                    requestNames.Contains(entry.Name) && !entry.CompletedAt.HasValue);
                settledSnapshots = hasRunningRequest ? 0 : settledSnapshots + 1;
                if (settledSnapshots == 2)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Zed did not complete all interactive requests through csls.{Environment.NewLine}{Describe(lastSnapshot)}",
                exception);
        }

        throw new InvalidOperationException("The trace settling loop ended unexpectedly.");
    }

    private static string Describe(ControlDashboardSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return "No dashboard snapshot completed before the deadline.";
        }

        ControlRequestSchedulerInfo requests = snapshot.Requests;
        string entries = string.Join(Environment.NewLine, requests.Trace.Entries.TakeLast(64).Select(static entry =>
            $"{entry.Ordinal}: {entry.Name} {entry.Status}; accepted={entry.AcceptedAt:O}; " +
            $"started={entry.StartedAt:O}; completed={entry.CompletedAt:O}; canceled={entry.IsCancellationRequested}; " +
            $"error={entry.ExceptionType}: {entry.ExceptionMessage}"));
        string active = string.Join(Environment.NewLine, requests.ActiveRequests.Take(32).Select(static entry =>
            $"{entry.Ordinal}: {entry.Name} {entry.Status}; accepted={entry.AcceptedAt:O}; started={entry.StartedAt:O}"));
        string logs = string.Join(Environment.NewLine, snapshot.Logs.TakeLast(16).Select(static entry =>
            $"{entry.Timestamp:O} {entry.Level} {entry.Category}: {entry.Message[..Math.Min(entry.Message.Length, 1024)]}"));
        return $"Session={snapshot.Session.LifecycleState}; phase={snapshot.Session.WorkspacePhase}; " +
            $"generation={snapshot.Session.WorkspaceGeneration}; projects={snapshot.Projects.Count}; documents={snapshot.Documents.Count}; " +
            $"queued={requests.QueuedRequests}; foreground={requests.ActiveForegroundRequests}; background={requests.ActiveBackgroundRequests}; " +
            $"mutation={requests.IsMutationActive}; stopping={requests.IsStopping}.{Environment.NewLine}" +
            $"Trace active={requests.Trace.IsActive}; retained={requests.Trace.Entries.Count}; dropped={requests.Trace.DroppedEntries}." +
            $"{Environment.NewLine}{entries}{Environment.NewLine}Active requests:{Environment.NewLine}{active}" +
            $"{Environment.NewLine}Recent logs:{Environment.NewLine}{logs}";
    }
}
