using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies failed-stop evidence through real adapter processes and stopped managed targets.
/// </summary>
[TestClass]
public sealed class DapStopDiagnosticsTests : DapTestContext
{
    /// <summary>
    /// Preserves read-only inspection responses and leaves a stopped target available to continue.
    /// </summary>
    /// <param name="cancelBeforeCapture">Whether cancellation precedes all diagnostic transport operations.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StoppedThreadDiagnosticsPreserveInspectionAndSession(bool cancelBeforeCapture)
    {
        string signal = Path.Join(Path.GetTempPath(), $"csls-stop-diagnostics-{Guid.NewGuid():N}.signal");
        try
        {
            await File.WriteAllTextAsync(signal, "release", TestContext.CancellationToken).ConfigureAwait(false);
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            using DapTestCancellationCapture cancellationLog = CaptureProtocolOnCancellation(client);
            string program = DebuggerLanguageFixtures.GetProgramPath("Csls.Debugger.Fixtures.CSharp", "Debug");
            (int threadId, _) = await LaunchAtEntryAsync(client, program, [signal, "41", "entry-result"])
                .ConfigureAwait(false);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            if (cancelBeforeCapture)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
            else
            {
                _ = await client.SendRequestAsync("threads", WriteEmptyObject, TestContext.CancellationToken)
                    .ConfigureAwait(false);
            }

            string before = client.ProtocolTranscript;
            string evidence = await DapStoppedThreadDiagnostics.CaptureAsync(client, threadId, cancellation.Token)
                .ConfigureAwait(false);
            if (cancelBeforeCapture)
            {
                Assert.Contains("Stop inspection failed: OperationCanceledException:", evidence);
                Assert.AreEqual(before, client.ProtocolTranscript,
                    "Pre-canceled diagnostic capture must leave the real transport untouched.");
            }
            else
            {
                string[] responses = evidence.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                Assert.HasCount(2, responses);
                using var exception = JsonDocument.Parse(responses[0]);
                Assert.AreEqual("exceptionInfo", exception.RootElement.GetProperty("command").GetString());
                Assert.IsFalse(exception.RootElement.GetProperty("success").GetBoolean());
                string? exceptionMessage = exception.RootElement.GetProperty("message").GetString();
                Assert.IsNotNull(exceptionMessage);
                Assert.Contains("exception", exceptionMessage, StringComparison.OrdinalIgnoreCase);
                using var stack = JsonDocument.Parse(responses[1]);
                Assert.AreEqual("stackTrace", stack.RootElement.GetProperty("command").GetString());
                Assert.IsTrue(stack.RootElement.GetProperty("success").GetBoolean());
                JsonElement frames = stack.RootElement.GetProperty("body").GetProperty("stackFrames");
                Assert.IsGreaterThan(0, frames.GetArrayLength());
                Assert.IsLessThanOrEqualTo(32, frames.GetArrayLength());
                string? frameName = frames[0].GetProperty("name").GetString();
                Assert.IsNotNull(frameName);
                Assert.Contains("Program.Main", frameName);
                Assert.AreEqual("Program.cs", frames[0].GetProperty("source").GetProperty("name").GetString());
            }

            string source = Path.Join(FindRepositoryRoot(), "test-assets", "Csls.Debugger.Fixtures.CSharp", "Program.cs");
            (string name, _, _) = await ReadSourceFrameAsync(client, threadId, source, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.Contains("Program.Main", name);
            await ContinueEntryToExitAsync(client, threadId, "entry-result").ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(signal);
        }
    }

    /// <summary>
    /// Retains the original wrong-stop assertion together with the actual target stack before cleanup.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task UnexpectedEntryStopRetainsOriginalFailure()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        using DapTestCancellationCapture cancellationLog = CaptureProtocolOnCancellation(client);
        int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, initialize, "initialize", success: true);
        }

        int launch = await client.SendRequestAsync("launch", writer => WriteLaunchArguments(
            writer, ResolveTestProcessHost(), ["--wait-for-standard-input"], wait: true,
            noDebug: false, stopAtEntry: true), TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertEvent(initialized.RootElement, "initialized");
        }

        int configuration = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        AssertFailedException failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(() =>
            ReadInitialBreakpointStopAsync(client, configuration, launch, TestContext.CancellationToken))
            .ConfigureAwait(false);
        Assert.Contains("breakpoint", failure.Message);
        Assert.Contains("\"reason\":\"entry\"", failure.Message);
        Assert.Contains("\"command\":\"exceptionInfo\"", failure.Message);
        Assert.Contains("\"stackFrames\":[{", failure.Message);
        Assert.Contains("\"name\":\"Program.\\u003CMain\\u003E$\"", failure.Message);
        await DisconnectAsync(client).ConfigureAwait(false);
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    /// <summary>
    /// Preserves both real inspection errors when no target has been configured.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task MissingTargetDiagnosticsPreserveAdapterFailures()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, initialize, "initialize", success: true);
        }

        string evidence = await DapStoppedThreadDiagnostics.CaptureAsync(client, 1, TestContext.CancellationToken)
            .ConfigureAwait(false);
        string[] responses = evidence.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(2, responses);
        string[] commands = ["exceptionInfo", "stackTrace"];
        for (int index = 0; index < responses.Length; index++)
        {
            using var response = JsonDocument.Parse(responses[index]);
            Assert.AreEqual(commands[index], response.RootElement.GetProperty("command").GetString());
            Assert.IsFalse(response.RootElement.GetProperty("success").GetBoolean());
            string? message = response.RootElement.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("Initialized", message);
        }

        await DisconnectAsync(client).ConfigureAwait(false);
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }
}
