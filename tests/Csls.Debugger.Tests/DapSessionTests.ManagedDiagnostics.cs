using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies managed failure diagnostics against a real adapter process and protocol connection.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Captures launcher stack symbols or honors cancellation while preserving protocol responses.
    /// </summary>
    /// <param name="cancelBeforeCapture">Whether collection is canceled before the diagnostic connection.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedStackCapturePreservesAdapterConnection(bool cancelBeforeCapture)
    {
        string path = Path.Join(Path.GetTempPath(), $"csls-managed-stacks-{Guid.NewGuid():N}.nettrace");
        try
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            int initialize = await client.SendRequestAsync("initialize", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertResponse(initialized.RootElement, initialize, "initialize", success: true);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            if (cancelBeforeCapture)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                _ = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                    DebuggerManagedStackCapture.CaptureAsync(client.HostProcessId, path, cancellation.Token))
                    .ConfigureAwait(false);
                Assert.IsFalse(File.Exists(path));
            }
            else
            {
                await DebuggerManagedStackCapture.CaptureAsync(client.HostProcessId, path, cancellation.Token)
                    .ConfigureAwait(false);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                byte[] trace = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(trace, TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsTrue(trace.AsSpan().StartsWith("Nettrace"u8));
                Assert.IsGreaterThanOrEqualTo(0, trace.AsSpan().IndexOf(Encoding.Unicode.GetBytes("csls, Version=")));
            }

            int threads = await client.SendRequestAsync("threads", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertResponse(response.RootElement, threads, "threads", success: false);
            Assert.Contains("Initialized", response.RootElement.GetProperty("message").GetString()!);
            await client.CloseProtocolAsync().ConfigureAwait(false);
            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
