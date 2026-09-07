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
    /// Rereads a changed environment file on restart and reports malformed replacements through the existing DAP connection.
    /// </summary>
    /// <param name="noDebug">Whether to launch without managed debugging.</param>
    /// <param name="invalidReplacement">Whether the replacement file contains malformed data.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RestartReloadsEnvironmentFile(bool noDebug, bool invalidReplacement)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-restart-environment-");
        string environmentFile = Path.Join(directory.FullName, ".env");
        string signalFile = Path.Join(directory.FullName, "release.signal");
        try
        {
            await File.WriteAllTextAsync(environmentFile, "CSLS_RESTART_VALUE=before", TestContext.CancellationToken)
                .ConfigureAwait(false);
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            using DapTestCancellationCapture cancellationLog = CaptureProtocolOnCancellation(client);
            int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, initialize, "initialize", success: true);
            }

            int launch = await client.SendRequestAsync("launch", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("program", ResolveTestProcessHost());
                writer.WriteString("cwd", directory.FullName);
                writer.WriteString("envFile", ".env");
                writer.WriteBoolean("noDebug", noDebug);
                writer.WriteStartArray("args");
                writer.WriteStringValue("--print-utf8-environment-and-wait-for-file");
                writer.WriteStringValue("CSLS_RESTART_VALUE");
                writer.WriteStringValue(signalFile);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertEvent(initialized.RootElement, "initialized");
            }

            int configuration = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            await ReadUntilResponseAsync(client, configuration, "configurationDone").ConfigureAwait(false);
            await ReadUntilResponseAsync(client, launch, "launch").ConfigureAwait(false);
            int firstProcess = await ReadEnvironmentProcessAsync(client, "before").ConfigureAwait(false);
            await File.WriteAllTextAsync(environmentFile,
                invalidReplacement ? "VALUE=\"sensitive-value" : "CSLS_RESTART_VALUE=after",
                TestContext.CancellationToken).ConfigureAwait(false);
            int restart = await client.SendRequestAsync("restart", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            using (JsonDocument exited = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertEvent(exited.RootElement, "exited");
            }

            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, restart, "restart", success: !invalidReplacement);
                if (invalidReplacement)
                {
                    Assert.Contains("envFile", response.RootElement.GetProperty("message").GetString()!);
                    Assert.DoesNotContain("sensitive-value", response.RootElement.ToString());
                }
            }

            await AssertProcessExitedAsync(firstProcess, TestContext.CancellationToken).ConfigureAwait(false);
            if (invalidReplacement)
            {
                using JsonDocument terminated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
                AssertEvent(terminated.RootElement, "terminated");
            }
            else
            {
                int replacementProcess = await ReadEnvironmentProcessAsync(client, "after").ConfigureAwait(false);
                Assert.AreNotEqual(firstProcess, replacementProcess);
                int disconnect = await client.SendRequestAsync("disconnect", WriteEmptyObject, TestContext.CancellationToken)
                    .ConfigureAwait(false);
                await ReadEnvironmentDisconnectAsync(client, disconnect).ConfigureAwait(false);
                await AssertProcessExitedAsync(replacementProcess, TestContext.CancellationToken).ConfigureAwait(false);
            }

            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.IsEmpty(client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(environmentFile);
            directory.Delete();
        }
    }

    private async Task ReadEnvironmentDisconnectAsync(DapTestClient client, int sequence)
    {
        try
        {
            await ReadEnvironmentDisconnectCoreAsync(client, sequence).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await DebuggerProcessDiagnostics.CaptureAsync(client.HostProcessId, TestContext).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReadEnvironmentDisconnectCoreAsync(DapTestClient client, int sequence)
    {
        bool exited = false;
        bool terminated = false;
        bool responded = false;
        while (!exited || !terminated || !responded)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                Assert.IsFalse(responded);
                AssertResponse(root, sequence, "disconnect", success: true);
                responded = true;
                continue;
            }

            Assert.AreEqual("event", root.GetProperty("type").GetString(), root.ToString());
            switch (root.GetProperty("event").GetString())
            {
                case "exited":
                    Assert.IsFalse(exited);
                    _ = root.GetProperty("body").GetProperty("exitCode").GetInt32();
                    exited = true;
                    break;
                case "terminated":
                    Assert.IsTrue(exited);
                    Assert.IsFalse(terminated);
                    terminated = true;
                    break;
                default:
                    Assert.Fail($"Unexpected environment target shutdown message: {root}");
                    break;
            }
        }
    }

    private async Task<int> ReadEnvironmentProcessAsync(DapTestClient client, string expected)
    {
        using JsonDocument process = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertEvent(process.RootElement, "process");
        int processId = process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32();
        Assert.IsGreaterThan(0, processId);
        var output = new System.Text.StringBuilder();
        while (output.Length < expected.Length)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertEvent(message.RootElement, "output");
            Assert.AreEqual("stdout", message.RootElement.GetProperty("body").GetProperty("category").GetString());
            output.Append(message.RootElement.GetProperty("body").GetProperty("output").GetString());
        }

        Assert.AreEqual(expected, output.ToString());
        return processId;
    }

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
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(
                testDirectory,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
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
