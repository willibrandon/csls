using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies DAP sequencing and target ownership through production sessions.
/// </summary>
[TestClass]
public sealed partial class DapSessionTests : DapTestContext
{
    /// <summary>
    /// Rejects invalid request ordering without preventing later initialization.
    /// </summary>
    [TestMethod]
    public async Task InvalidStateRequestDoesNotCorruptSession()
    {
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        int threadsSequence = await client.SendRequestAsync(
            "threads",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument threads = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(threads.RootElement, threadsSequence, "threads", success: false);
        Assert.Contains(
            "Created",
            threads.RootElement.GetProperty("message").GetString()!,
            StringComparison.Ordinal);

        int initializeSequence = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);

        int repeatedSequence = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument repeated = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(repeated.RootElement, repeatedSequence, "initialize", success: false);
        Assert.Contains(
            "Initialized",
            repeated.RootElement.GetProperty("message").GetString()!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects an unadvertised graceful-terminate request without ending the connection.
    /// </summary>
    [TestMethod]
    public async Task UnadvertisedTerminateRequestReturnsUnsupportedFailure()
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
        int terminateSequence = await client.SendRequestAsync(
            "terminate",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument terminate = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);

        AssertResponse(terminate.RootElement, terminateSequence, "terminate", success: false);
        Assert.Contains(
            "not supported",
            terminate.RootElement.GetProperty("message").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Terminates a real long-running target when its DAP owner disconnects.
    /// </summary>
    [TestMethod]
    public async Task DisconnectTerminatesRunningOwnedProcessTree()
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
                ["--wait-for-standard-input"],
                wait: true),
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
        int processId = process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32();

        int disconnectSequence = await client.SendRequestAsync(
            "disconnect",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        JsonDocument message;
        do
        {
            message = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            if (message.RootElement.TryGetProperty("request_seq", out JsonElement requestSequence) &&
                requestSequence.GetInt32() == disconnectSequence)
            {
                break;
            }

            message.Dispose();
        }
        while (true);
        using (message)
        {
            AssertResponse(message.RootElement, disconnectSequence, "disconnect", success: true);
        }

        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        await AssertProcessExitedAsync(processId, TestContext.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Terminates an owned target when the adapter connection is canceled without disconnect.
    /// </summary>
    [TestMethod]
    public async Task ConnectionCancellationTerminatesRunningOwnedProcessTree()
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
                ["--wait-for-standard-input"],
                wait: true),
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
        int processId = process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32();

        await client.DisposeAsync().ConfigureAwait(false);

        await AssertProcessExitedAsync(processId, TestContext.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Terminates an owned target when the client closes its protocol stream abruptly.
    /// </summary>
    [TestMethod]
    public async Task EndOfStreamTerminatesRunningOwnedProcessTree()
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
                ["--wait-for-standard-input"],
                wait: true),
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
        int processId = process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32();

        await client.CloseProtocolAsync().ConfigureAwait(false);

        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        await AssertProcessExitedAsync(processId, TestContext.CancellationToken).ConfigureAwait(false);
    }

    private static async Task AssertProcessExitedAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        }

        Assert.Fail($"Debugger-owned process {processId} remained alive after disconnect.");
    }
}
