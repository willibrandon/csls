using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies pause acknowledgement ordering alongside real target output.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Acknowledges pause before the stopped event while preserving output already queued on the transport.
    /// </summary>
    /// <param name="exitWhileStopped">Whether an external process exit ends the paused session.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task PauseAcknowledgesBeforeStoppedWithQueuedTargetOutput(bool exitWhileStopped)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int initializeSequence = await client.SendRequestAsync(
                "initialize", WriteEmptyObject, TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialize = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);
            int launchSequence = await client.SendRequestAsync(
                "launch",
                writer => WriteLaunchArguments(
                    writer, ResolveTestProcessHost(), ["--debugger-fixture", waitPath],
                    wait: true, noDebug: false),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(initialized.RootElement, "initialized");
            int configurationSequence = await client.SendRequestAsync(
                "configurationDone", WriteEmptyObject, TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument configuration = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(configuration.RootElement, configurationSequence, "configurationDone", success: true);
            using JsonDocument launch = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(launch.RootElement, launchSequence, "launch", success: true);
            using JsonDocument process = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(process.RootElement, "process");

            JsonElement pendingOutput = await client.PeekMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(pendingOutput, "output");
            Assert.AreEqual("stdout", pendingOutput.GetProperty("body").GetProperty("category").GetString());
            Assert.AreEqual("ready", pendingOutput.GetProperty("body").GetProperty("output").GetString());
            await PauseFixtureAsync(client).ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            Assert.IsGreaterThan(0, frame.GetProperty("id").GetInt32());
            int repeatedPauseSequence = await client.SendRequestAsync(
                "pause", WriteEmptyObject, TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument repeatedPause = await ReadExecutionControlMessageAsync(client)
                .ConfigureAwait(false);
            AssertResponse(repeatedPause.RootElement, repeatedPauseSequence, "pause", success: true);
            // The next inspection must keep its frame and must not encounter a duplicate stopped event.
            JsonElement unchangedFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            AssertSameLogicalFrame(frame, unchangedFrame);
            if (exitWhileStopped)
            {
                int processId = process.RootElement.GetProperty("body")
                    .GetProperty("systemProcessId").GetInt32();
                using var target = Process.GetProcessById(processId);
                target.Kill();
                await target.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
                using JsonDocument exited = await ReadExecutionControlMessageAsync(client)
                    .ConfigureAwait(false);
                AssertEvent(exited.RootElement, "exited");
                using JsonDocument terminated = await ReadExecutionControlMessageAsync(client)
                    .ConfigureAwait(false);
                AssertEvent(terminated.RootElement, "terminated");
                Assert.IsGreaterThan(
                    exited.RootElement.GetProperty("seq").GetInt32(),
                    terminated.RootElement.GetProperty("seq").GetInt32());
                TestContext.WriteLine("Received exited and terminated; awaiting adapter shutdown.");
                Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false));
                _ = await Assert.ThrowsExactlyAsync<EndOfStreamException>(async () =>
                {
                    using JsonDocument unexpected = await client.ReadMessageAsync(
                        TestContext.CancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            else
            {
                await ResumeAndReleaseFixtureAsync(client, waitPath).ConfigureAwait(false);
            }
        }
        finally
        {
            File.Delete(waitPath);
        }
    }
}
