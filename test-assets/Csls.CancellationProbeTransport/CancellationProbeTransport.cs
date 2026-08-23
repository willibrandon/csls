using System.IO;
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
        File.WriteAllText(markerPath, "started");
        cancellationToken.WaitHandle.WaitOne();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            File.WriteAllText(markerPath, "canceled");
        }
    }
}
