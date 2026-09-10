using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies that idle Unix protocol input leaves worker capacity available for responses.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Receives responses before sending more input when the adapter has one thread-pool worker.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task IdleProtocolInputDoesNotBlockResponses()
    {
        // Constrain only this owned adapter, leaving the test runner's scheduling unchanged.
        Dictionary<string, string?> environment = new(StringComparer.Ordinal)
        {
            ["DOTNET_ThreadPool_ForceMinWorkerThreads"] = "1",
            ["DOTNET_ThreadPool_ForceMaxWorkerThreads"] = "1"
        };
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken, environment)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        using (CaptureProtocolOnCancellation(client))
        {
            int initialize = await client.SendRequestAsync("initialize", WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, initialize, "initialize", success: true);
            }

            for (int index = 0; index < 16; index++)
            {
                int sequence = await client.SendRequestAsync("threads", WriteEmptyObject,
                    TestContext.CancellationToken).ConfigureAwait(false);
                using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                AssertResponse(response.RootElement, sequence, "threads", success: false);
                Assert.Contains("Initialized", response.RootElement.GetProperty("message").GetString()!,
                    StringComparison.Ordinal);
            }

            await client.CloseProtocolAsync().ConfigureAwait(false);
            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
    }
}
