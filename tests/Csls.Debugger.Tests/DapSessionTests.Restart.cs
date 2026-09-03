using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies DAP target restart through real adapter and target processes.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Replaces a running managed target while retaining the adapter connection.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RestartReplacesManagedTargetWithLatestLaunchArguments()
    {
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        string firstSignal = Path.Join(testDirectory, "first.signal");
        string secondSignal = Path.Join(testDirectory, "second.signal");
        try
        {
            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
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
            Assert.IsTrue(initialize.RootElement.GetProperty("body")
                .GetProperty("supportsRestartRequest").GetBoolean());

            int launchSequence = await client.SendRequestAsync(
                "launch",
                writer => WriteLaunchArguments(
                    writer,
                    ResolveTestProcessHost(),
                    ["--debugger-fixture", firstSignal],
                    wait: true,
                    noDebug: false),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialized = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(initialized.RootElement, "initialized");
            int configurationSequence = await client.SendRequestAsync(
                "configurationDone",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument configuration = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument launch = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument firstProcess = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument firstReady = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                configuration.RootElement,
                configurationSequence,
                "configurationDone",
                success: true);
            AssertResponse(launch.RootElement, launchSequence, "launch", success: true);
            AssertEvent(firstProcess.RootElement, "process");
            AssertEvent(firstReady.RootElement, "output");
            int firstProcessId = firstProcess.RootElement.GetProperty("body")
                .GetProperty("systemProcessId").GetInt32();

            int invalidRestartSequence = await client.SendRequestAsync(
                "restart",
                static writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNull("arguments");
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument invalidRestart = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                invalidRestart.RootElement,
                invalidRestartSequence,
                "restart",
                success: false);
            int threadsSequence = await client.SendRequestAsync(
                "threads",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument threads = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(threads.RootElement, threadsSequence, "threads", success: true);
            using (var originalProcess = Process.GetProcessById(firstProcessId))
            {
                Assert.IsFalse(originalProcess.HasExited);
            }

            int restartSequence = await client.SendRequestAsync(
                "restart",
                writer => WriteRestartArguments(writer, secondSignal),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument exited = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument restart = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument secondProcess = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument secondReady = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(exited.RootElement, "exited");
            AssertResponse(restart.RootElement, restartSequence, "restart", success: true);
            AssertEvent(secondProcess.RootElement, "process");
            AssertEvent(secondReady.RootElement, "output");
            int secondProcessId = secondProcess.RootElement.GetProperty("body")
                .GetProperty("systemProcessId").GetInt32();
            Assert.AreNotEqual(firstProcessId, secondProcessId);
            await AssertProcessExitedAsync(firstProcessId, TestContext.CancellationToken)
                .ConfigureAwait(false);

            int disconnectSequence = await client.SendRequestAsync(
                "disconnect",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            await ReadUntilResponseAsync(
                client,
                disconnectSequence,
                "disconnect").ConfigureAwait(false);
            Assert.AreEqual(
                0,
                await client.WaitForExitAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false));
            await AssertProcessExitedAsync(secondProcessId, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    private static void WriteRestartArguments(Utf8JsonWriter writer, string signalPath)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("arguments");
        WriteLaunchArguments(
            writer,
            ResolveTestProcessHost(),
            ["--debugger-fixture", signalPath],
            wait: true,
            noDebug: false);
        writer.WriteEndObject();
    }

    private async Task ReadUntilResponseAsync(
        DapTestClient client,
        int sequence,
        string command)
    {
        while (true)
        {
            using JsonDocument message = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            if (message.RootElement.GetProperty("type").GetString() == "response" &&
                message.RootElement.GetProperty("request_seq").GetInt32() == sequence)
            {
                AssertResponse(message.RootElement, sequence, command, success: true);
                return;
            }
        }
    }
}
