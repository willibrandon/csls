using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies consumer identity while another async iterator resumes and the target collects its heap.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Ignores a competing consumer that completes before the selected iterator can yield its value.
    /// </summary>
    /// <param name="configuration">The compiler optimization configuration.</param>
    [TestMethod]
    [DataRow("Debug")]
    [DataRow("Release")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task AsyncIteratorYieldTracksSelectedConsumerAcrossCollection(string configuration)
    {
        string sourcePath = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "DebuggerAsyncIteratorStepFixture.cs");
        string[] lines = await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken).ConfigureAwait(false);
        int awaitLine = FindSourceLine(lines, "await pipe.ReadExactlyAsync");
        int yieldLine = FindSourceLine(lines, "yield return CollectAndReturn");
        int consumerLine = FindSourceLine(lines, "await foreach");
        string pipeName = $"cc-{Guid.NewGuid():N}";
        using var selected = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var competing = new NamedPipeServerStream(pipeName + "-c", PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var connections = Task.WhenAll(selected.WaitForConnectionAsync(TestContext.CancellationToken),
            competing.WaitForConnectionAsync(TestContext.CancellationToken));
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
        int initialThread = await LaunchToSourceBreakpointAsync(client, sourcePath, awaitLine,
            ["--debugger-concurrent-async-iterator-step-fixture", pipeName],
            ResolveAsyncIteratorProgram(configuration), suppressJitOptimizations: true).ConfigureAwait(false);
        await connections.ConfigureAwait(false);
        await ClearSourceBreakpointsAsync(client, sourcePath).ConfigureAwait(false);
        Task<int> initialStep = StepAndReadStopAsync(client, "next", initialThread, TestContext.CancellationToken);
        byte[] handshake = new byte[1];
        await selected.ReadExactlyAsync(handshake, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual((byte)1, handshake[0]);
        await selected.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
        int resumedThread = await initialStep.ConfigureAwait(false);
        Assert.AreNotEqual(initialThread, resumedThread);
        _ = await AssertSourcePolicyBreakpointAsync(client, sourcePath, yieldLine, verified: true).ConfigureAwait(false);
        int yieldThread = await ContinueEntryToUserBreakpointAsync(client).ConfigureAwait(false);
        await ClearSourceBreakpointsAsync(client, sourcePath).ConfigureAwait(false);

        Task<int> consumerStep = StepAndReadStopAsync(client, "next", yieldThread, TestContext.CancellationToken);
        await selected.ReadExactlyAsync(handshake, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual((byte)2, handshake[0]);
        await competing.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
        Task competitorFinished = selected.ReadExactlyAsync(handshake, TestContext.CancellationToken).AsTask();
        Task first = await Task.WhenAny(consumerStep, competitorFinished).ConfigureAwait(false);
        Assert.AreSame(competitorFinished, first,
            "The selected step stopped before the competing consumer finished and released its continuation.");
        await competitorFinished.ConfigureAwait(false);
        Assert.AreEqual((byte)3, handshake[0]);
        await selected.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
        int consumerThread = await consumerStep.ConfigureAwait(false);
        await AssertAsyncIteratorFrameAsync(client, consumerThread, sourcePath, consumerLine,
            "ConsumeAsync", "total", "0").ConfigureAwait(false);
        JsonElement frame = await ReadTopSourceFrameAsync(client, consumerThread).ConfigureAwait(false);
        JsonElement identity = await ReadEvaluationAsync(client, frame.GetProperty("id").GetInt32(),
            "identity", success: true, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("1", identity.GetProperty("result").GetString());
        int continueSequence = await client.SendRequestAsync("continue", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        await ReadSuccessfulTerminationAsync(client, continueSequence, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }
}
