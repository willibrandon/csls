using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies Debug Adapter Protocol request cancellation behavior.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Rejects malformed cancellation without faulting the protocol session.
    /// </summary>
    [TestMethod]
    public async Task MalformedCancelRequestDoesNotFaultSession()
    {
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        int initializeSequence = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(
            initialize.RootElement,
            initializeSequence,
            "initialize",
            success: true);

        int cancelSequence = await client.SendRequestAsync(
            "cancel",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("requestId", 0);
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument cancel = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(cancel.RootElement, cancelSequence, "cancel", success: false);

        int threadsSequence = await client.SendRequestAsync(
            "threads",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument threads = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(threads.RootElement, threadsSequence, "threads", success: false);
        Assert.Contains(
            "Initialized",
            threads.RootElement.GetProperty("message").GetString()!,
            StringComparison.Ordinal);
    }
}
