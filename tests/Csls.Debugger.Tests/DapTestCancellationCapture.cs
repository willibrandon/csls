namespace Csls.Debugger.Tests;

/// <summary>
/// Retains the real client's bounded protocol exchange when a canceled test unwinds.
/// </summary>
/// <param name="client">The client kept alive until this scope is disposed.</param>
/// <param name="testName">The owning test's diagnostic name.</param>
/// <param name="path">The unique artifact path within the test results directory.</param>
/// <param name="cancellationToken">The framework-owned test cancellation token.</param>
internal sealed class DapTestCancellationCapture(
    DapTestClient client,
    string? testName,
    string path,
    CancellationToken cancellationToken) : IDisposable
{
    /// <inheritdoc />
    public void Dispose()
    {
        if (cancellationToken.IsCancellationRequested)
        {
            File.WriteAllText(path,
                $"{testName}, adapter {client.HostProcessId}{Environment.NewLine}{client.ProtocolTranscript}");
        }
    }
}
