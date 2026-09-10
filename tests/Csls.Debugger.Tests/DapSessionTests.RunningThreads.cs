using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies debugger requests that clients issue while a target is running.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Returns an empty thread snapshot while a real managed target is running.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RunningManagedTargetAcceptsThreadsRequest()
    {
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-running-threads-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
            _ = await client.SendRequestAsync(
                "initialize",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialize = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            _ = await client.SendRequestAsync(
                "launch",
                writer => WriteLaunchArguments(
                    writer,
                    ResolveTestProcessHost(),
                    ["--debugger-fixture", waitPath],
                    wait: true,
                    noDebug: false),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialized = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            _ = await client.SendRequestAsync(
                "configurationDone",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument configuration = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument launch = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument process = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument ready = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(ready.RootElement, "output");

            int threadsSequence = await client.SendRequestAsync(
                "threads",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument threads = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(threads.RootElement, threadsSequence, "threads", success: true);
            Assert.IsEmpty(
                threads.RootElement
                    .GetProperty("body")
                    .GetProperty("threads")
                    .EnumerateArray()
                    .ToArray());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }
}
