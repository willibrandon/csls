using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies managed exception policy and inspection through a real DAP session.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Stops on a caught first-chance exception and reports its managed type.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ThrownExceptionFilterStopsAndReportsExceptionInfo()
    {
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-exception-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            await ExerciseThrownExceptionAsync(testDirectory).ConfigureAwait(false);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(
                testDirectory,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task ExerciseThrownExceptionAsync(string testDirectory)
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
        int launchSequence = await client.SendRequestAsync(
            "launch",
            writer => WriteLaunchArguments(
                writer,
                ResolveTestProcessHost(),
                ["--debugger-exception-fixture", Path.Join(testDirectory, "continue.signal")],
                wait: true,
                noDebug: false),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");

        int exceptionsSequence = await client.SendRequestAsync(
            "setExceptionBreakpoints",
            WriteThrownExceptionFilter,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument exceptions = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(
            exceptions.RootElement,
            exceptionsSequence,
            "setExceptionBreakpoints",
            success: true);
        int configurationSequence = await client.SendRequestAsync(
            "configurationDone",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        int threadId = await ReadExceptionStopAsync(
            client,
            launchSequence,
            configurationSequence).ConfigureAwait(false);
        await AssertExceptionInfoAsync(client, threadId).ConfigureAwait(false);
        await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    private async Task<int> ReadExceptionStopAsync(
        DapTestClient client,
        int launchSequence,
        int configurationSequence)
    {
        bool launchReceived = false;
        bool configurationReceived = false;
        bool processReceived = false;
        int? threadId = null;
        while (!launchReceived || !configurationReceived || !processReceived || threadId is null)
        {
            using JsonDocument message = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                int requestSequence = root.GetProperty("request_seq").GetInt32();
                if (requestSequence == launchSequence)
                {
                    AssertResponse(root, launchSequence, "launch", success: true);
                    launchReceived = true;
                }
                else if (requestSequence == configurationSequence)
                {
                    AssertResponse(
                        root,
                        configurationSequence,
                        "configurationDone",
                        success: true);
                    configurationReceived = true;
                }

                continue;
            }

            string? eventName = root.GetProperty("event").GetString();
            processReceived |= eventName == "process";
            if (eventName == "stopped")
            {
                JsonElement body = root.GetProperty("body");
                Assert.AreEqual("exception", body.GetProperty("reason").GetString());
                Assert.IsTrue(body.GetProperty("allThreadsStopped").GetBoolean());
                threadId = body.GetProperty("threadId").GetInt32();
            }
        }

        return threadId.Value;
    }

    private async Task AssertExceptionInfoAsync(DapTestClient client, int threadId)
    {
        int sequence = await client.SendRequestAsync(
            "exceptionInfo",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("threadId", threadId);
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "exceptionInfo", success: true);
        JsonElement body = response.RootElement.GetProperty("body");
        Assert.AreEqual("System.InvalidOperationException", body.GetProperty("exceptionId").GetString());
        Assert.AreEqual("always", body.GetProperty("breakMode").GetString());
        Assert.Contains(
            "System.InvalidOperationException",
            body.GetProperty("description").GetString()!,
            StringComparison.Ordinal);
        Assert.AreEqual(
            "System.InvalidOperationException",
            body.GetProperty("details").GetProperty("typeName").GetString());
    }

    private static void WriteThrownExceptionFilter(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("filters");
        writer.WriteStringValue("all");
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
