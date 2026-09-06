using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies pausing retires an asynchronous step while the selected callee is still running.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Continues normally after pausing an asynchronous Step Out with an armed caller continuation.
    /// </summary>
    [TestMethod]
    [DataRow("Debug", "Task")]
    [DataRow("Release", "Task")]
    [DataRow("Debug", "ValueTask")]
    [DataRow("Release", "ValueTask")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task PauseRetiresPendingAsyncStepOut(string configuration, string kind)
    {
        string sourcePath = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "DebuggerAsyncStepOutFixture.cs");
        string[] lines = await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken).ConfigureAwait(false);
        string marker = kind == "Task" ? "// task" : "// value task";
        int awaitLine = FindSourceLine(lines, marker + " suspension");
        string pipeName = $"cp-{Guid.NewGuid():N}";
        using var selected = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var competing = new NamedPipeServerStream(pipeName + "-c", PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var connections = Task.WhenAll(selected.WaitForConnectionAsync(TestContext.CancellationToken),
            competing.WaitForConnectionAsync(TestContext.CancellationToken));
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        int initialThread = await LaunchToSourceBreakpointAsync(client, sourcePath, awaitLine,
            ["--debugger-concurrent-async-step-out-fixture", pipeName, kind], ResolveAsyncIteratorProgram(configuration),
            suppressJitOptimizations: true).ConfigureAwait(false);
        await connections.ConfigureAwait(false);
        await ClearSourceBreakpointsAsync(client, sourcePath).ConfigureAwait(false);
        Task<int> initialStep = StepAndReadStopAsync(client, "next", initialThread, TestContext.CancellationToken);
        byte[] handshake = new byte[1];
        await selected.ReadExactlyAsync(handshake, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual((byte)1, handshake[0]);
        await selected.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
        int resumedThread = await initialStep.ConfigureAwait(false);
        Assert.AreNotEqual(initialThread, resumedThread);
        await AssertAsyncStepOutFrameAsync(client, resumedThread, sourcePath, FindSourceLine(lines, marker + " resumption"),
            "Read" + kind + "Async", "40").ConfigureAwait(false);
        int stepSequence = await client.SendRequestAsync("stepOut", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("threadId", resumedThread);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument continued = await ReadExecutionControlMessageAsync(client).ConfigureAwait(false);
        AssertEvent(continued.RootElement, "continued");
        using JsonDocument stepResponse = await ReadExecutionControlMessageAsync(client).ConfigureAwait(false);
        AssertResponse(stepResponse.RootElement, stepSequence, "stepOut", success: true);
        await selected.ReadExactlyAsync(handshake, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual((byte)2, handshake[0]);

        await PauseFixtureAsync(client).ConfigureAwait(false);
        int continueSequence = await client.SendRequestAsync("continue", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        await competing.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
        await selected.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
        // Complete the duplex handshake before awaiting exit: the fixture awaits this write.
        await selected.ReadExactlyAsync(handshake, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual((byte)3, handshake[0]);
        await ReadSuccessfulTerminationAsync(client, continueSequence, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }
}
