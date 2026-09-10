using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies diagnostic capture through real adapter exchanges and independently owned artifact files.
/// </summary>
[TestClass]
public sealed class DapTestCancellationCaptureTests : DapTestContext
{
    /// <summary>
    /// Captures cancellation before the scope unwinds and retains the final exchange before client cleanup.
    /// </summary>
    /// <param name="cancelBeforeDisposal">Whether to cancel while the diagnostic scope is still active.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task CaptureRetainsExchangeBeforeClientCleanup(bool cancelBeforeDisposal)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-dap-capture-");
        string path = Path.Join(directory.FullName, "protocol.log");
        try
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            using (new DapTestCancellationCapture(client, TestContext.TestName, path, cancellation.Token))
            {
                int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
                using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
                AssertResponse(response.RootElement, initialize, "initialize", success: true);
                Assert.IsFalse(File.Exists(path));
                if (cancelBeforeDisposal)
                {
                    await cancellation.CancelAsync().ConfigureAwait(false);
                    string activeCapture = await File.ReadAllTextAsync(path, TestContext.CancellationToken).ConfigureAwait(false);
                    Assert.AreEqual(ExpectedCapture(client), activeCapture);
                }
            }

            string completedCapture = await File.ReadAllTextAsync(path, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(ExpectedCapture(client), completedCapture);
            int threads = await client.SendRequestAsync("threads", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument threadsResponse = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertResponse(threadsResponse.RootElement, threads, "threads", success: false);
            await cancellation.CancelAsync().ConfigureAwait(false);
            Assert.AreNotEqual(ExpectedCapture(client), completedCapture);
            Assert.AreEqual(completedCapture,
                await File.ReadAllTextAsync(path, TestContext.CancellationToken).ConfigureAwait(false));
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    private string ExpectedCapture(DapTestClient client) =>
        $"{TestContext.TestName}, adapter {client.HostProcessId}{Environment.NewLine}{client.ProtocolTranscript}";
}
