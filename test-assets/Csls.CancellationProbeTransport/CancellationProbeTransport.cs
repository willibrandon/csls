using System.Threading;

namespace Csls.Testing;

/// <summary>
/// Signals real analyzer lifecycle events through an observable file boundary.
/// </summary>
public static class CancellationProbeTransport
{
    /// <summary>
    /// Signals analyzer start, waits for cancellation, and signals cancellation delivery.
    /// </summary>
    /// <param name="markerPath">The absolute lifecycle marker path.</param>
    /// <param name="cancellationToken">The real Roslyn analyzer cancellation token.</param>
    public static void WaitForCancellation(
        string markerPath,
        CancellationToken cancellationToken)
    {
        FileSignalPublisher.WriteAllText(markerPath, "started");
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => FileSignalPublisher.WriteAllText((string)state, "canceled"),
            markerPath);
        cancellationToken.WaitHandle.WaitOne();
        cancellationToken.ThrowIfCancellationRequested();
    }
}
