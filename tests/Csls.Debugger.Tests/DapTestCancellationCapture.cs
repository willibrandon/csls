namespace Csls.Debugger.Tests;

/// <summary>
/// Retains the real client's bounded protocol exchange on cancellation and before client cleanup.
/// </summary>
internal sealed class DapTestCancellationCapture : IDisposable
{
    private readonly DapTestClient _client;
    private readonly string? _testName;
    private readonly string _path;
    private readonly int _processId;
    private readonly CancellationTokenRegistration _registration;

    /// <summary>
    /// Observes cancellation independently of the test's ability to unwind its pending operation.
    /// </summary>
    /// <param name="client">The client kept alive until this scope is disposed.</param>
    /// <param name="testName">The owning test's diagnostic name.</param>
    /// <param name="path">The unique artifact path within the test results directory.</param>
    /// <param name="cancellationToken">The framework-owned test cancellation token.</param>
    internal DapTestCancellationCapture(DapTestClient client, string? testName, string path,
        CancellationToken cancellationToken)
    {
        _client = client;
        _testName = testName;
        _path = path;
        _processId = client.HostProcessId;
        _registration = cancellationToken.Register(Capture);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _registration.Dispose();
        Capture();
    }

    private void Capture() => File.WriteAllText(_path,
        $"{_testName}, adapter {_processId}{Environment.NewLine}{_client.ProtocolTranscript}");
}
