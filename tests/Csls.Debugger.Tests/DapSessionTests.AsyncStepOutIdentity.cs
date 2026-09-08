using Microsoft.Diagnostics.NETCore.Client;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies asynchronous Step Out retains caller identity and obeys user breakpoint interruptions.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Follows the selected caller through compacting collection while a competing caller completes first.
    /// </summary>
    [TestMethod]
    [DataRow("Debug", "Task")]
    [DataRow("Release", "Task")]
    [DataRow("Debug", "ValueTask")]
    [DataRow("Release", "ValueTask")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task AsyncStepOutPreservesCallerIdentityAcrossCollection(string configuration, string kind)
    {
        string sourcePath = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "DebuggerAsyncStepOutFixture.cs");
        string[] lines = await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken).ConfigureAwait(false);
        string marker = kind == "Task" ? "// task" : "// value task";
        int awaitLine = FindSourceLine(lines, marker + " suspension");
        int callerLine = FindSourceLine(lines, marker + " caller");
        string pipeName = $"cx-{Guid.NewGuid():N}";
        using var selected = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var competing = new NamedPipeServerStream(pipeName + "-c", PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var connections = Task.WhenAll(selected.WaitForConnectionAsync(TestContext.CancellationToken),
            competing.WaitForConnectionAsync(TestContext.CancellationToken));
        var crashReports = new DebuggerCrashReportCapture(TestContext, DumpType.WithHeap);
        await using ConfiguredAsyncDisposable reportDisposal = crashReports.ConfigureAwait(false);
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken,
            environment: crashReports.Variables).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        using DapTestCancellationCapture cancellationLog = CaptureProtocolOnCancellation(client);
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

        Task<int> callerStep = StepAndReadStopAsync(client, "stepOut", resumedThread, TestContext.CancellationToken);
        await selected.ReadExactlyAsync(handshake, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual((byte)2, handshake[0]);
        await competing.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
        Task competitorFinished = selected.ReadExactlyAsync(handshake, TestContext.CancellationToken).AsTask();
        Task first = await Task.WhenAny(callerStep, competitorFinished).ConfigureAwait(false);
        Assert.AreSame(competitorFinished, first,
            "Step Out stopped before the competing caller finished and released the selected caller.");
        await competitorFinished.ConfigureAwait(false);
        Assert.AreEqual((byte)3, handshake[0]);
        await selected.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
        int callerThread = await callerStep.ConfigureAwait(false);
        await AssertAsyncStepOutFrameAsync(client, callerThread, sourcePath, callerLine,
            "Consume" + kind + "Async", "42").ConfigureAwait(false);
        JsonElement frame = await ReadTopSourceFrameAsync(client, callerThread).ConfigureAwait(false);
        JsonElement identity = await ReadEvaluationAsync(client, frame.GetProperty("id").GetInt32(), "identity",
            success: true, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("1", identity.GetProperty("result").GetString());
        int continueSequence = await client.SendRequestAsync("continue", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        await ReadSuccessfulTerminationAsync(client, continueSequence, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    /// <summary>
    /// Retires the pending caller step when a user breakpoint interrupts the callee's finally block.
    /// </summary>
    [TestMethod]
    [DataRow("Debug", "Task")]
    [DataRow("Release", "Task")]
    [DataRow("Debug", "ValueTask")]
    [DataRow("Release", "ValueTask")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task FinallyBreakpointInterruptsAsyncStepOut(string configuration, string kind)
    {
        string sourcePath = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "DebuggerAsyncStepOutFixture.cs");
        string[] lines = await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken).ConfigureAwait(false);
        string marker = kind == "Task" ? "// task" : "// value task";
        int awaitLine = FindSourceLine(lines, marker + " suspension");
        int finallyLine = FindSourceLine(lines, marker + " finally");
        string pipeName = $"cf-{Guid.NewGuid():N}";
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Task connection = pipe.WaitForConnectionAsync(TestContext.CancellationToken);
        var crashReports = new DebuggerCrashReportCapture(TestContext, DumpType.Normal);
        await using ConfiguredAsyncDisposable reportDisposal = crashReports.ConfigureAwait(false);
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken,
            environment: crashReports.Variables).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        int initialThread = await LaunchToSourceBreakpointAsync(client, sourcePath, awaitLine,
            ["--debugger-async-step-out-fixture", pipeName, kind], ResolveAsyncIteratorProgram(configuration),
            suppressJitOptimizations: true).ConfigureAwait(false);
        await connection.ConfigureAwait(false);
        Task<int> initialStep = StepAndReadStopAsync(client, "next", initialThread, TestContext.CancellationToken);
        byte[] handshake = new byte[1];
        await pipe.ReadExactlyAsync(handshake, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual((byte)1, handshake[0]);
        await pipe.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
        int resumedThread = await initialStep.ConfigureAwait(false);
        Assert.AreNotEqual(initialThread, resumedThread);
        _ = await AssertSourcePolicyBreakpointAsync(client, sourcePath, finallyLine, verified: true).ConfigureAwait(false);
        int finallyThread = await StepAndReadStopAsync(client, "stepOut", resumedThread,
            TestContext.CancellationToken, expectedReason: "breakpoint").ConfigureAwait(false);
        await AssertAsyncStepOutFrameAsync(client, finallyThread, sourcePath, finallyLine,
            "Read" + kind + "Async", "41").ConfigureAwait(false);
        await ClearSourceBreakpointsAsync(client, sourcePath).ConfigureAwait(false);
        int continueSequence = await client.SendRequestAsync("continue", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        await ReadSuccessfulTerminationAsync(client, continueSequence, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }
}
