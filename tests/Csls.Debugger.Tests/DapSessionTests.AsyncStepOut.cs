using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies asynchronous Step Out follows the awaiting caller through a real suspension.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Returns from a resumed asynchronous callee to its caller's next authored statement.
    /// </summary>
    [TestMethod]
    [DataRow("Debug", "Task", false)]
    [DataRow("Release", "Task", false)]
    [DataRow("Debug", "ValueTask", false)]
    [DataRow("Release", "ValueTask", false)]
    [DataRow("Debug", "Task", true)]
    [DataRow("Release", "Task", true)]
    [DataRow("Debug", "ValueTask", true)]
    [DataRow("Release", "ValueTask", true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StepOutOfResumedAsyncMethodStopsInAwaitingCaller(string configuration, string kind, bool scheduled)
    {
        string sourcePath = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "DebuggerAsyncStepOutFixture.cs");
        string[] lines = await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken).ConfigureAwait(false);
        string marker = kind == "Task" ? "// task" : "// value task";
        int awaitLine = FindSourceLine(lines, marker + " suspension");
        int resumedLine = FindSourceLine(lines, marker + " resumption");
        int callerLine = FindSourceLine(lines, marker + " caller");
        string pipeName = $"co-{Guid.NewGuid():N}";
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Task connection = pipe.WaitForConnectionAsync(TestContext.CancellationToken);
        var crashReports = new DebuggerCrashReportCapture(TestContext, captureMemory: true);
        await using ConfiguredAsyncDisposable reportDisposal = crashReports.ConfigureAwait(false);
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken,
            environment: crashReports.Variables).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        string mode = scheduled ? "--debugger-scheduled-async-step-out-fixture" : "--debugger-async-step-out-fixture";
        int initialThread = await LaunchToSourceBreakpointAsync(client, sourcePath, awaitLine,
            [mode, pipeName, kind], ResolveAsyncIteratorProgram(configuration),
            suppressJitOptimizations: true).ConfigureAwait(false);
        await connection.ConfigureAwait(false);

        Task<int> stepping = StepAndReadStopAsync(client, "next", initialThread, TestContext.CancellationToken);
        byte[] suspended = new byte[1];
        await pipe.ReadExactlyAsync(suspended, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual((byte)1, suspended[0]);
        await pipe.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
        int resumedThread = await stepping.ConfigureAwait(false);
        if (!scheduled)
        {
            Assert.AreNotEqual(initialThread, resumedThread);
        }
        await AssertAsyncStepOutFrameAsync(client, resumedThread, sourcePath, resumedLine,
            "Read" + kind + "Async", "40").ConfigureAwait(false);
        await ClearSourceBreakpointsAsync(client, sourcePath).ConfigureAwait(false);
        int callerThread = await StepAndReadStopAsync(client, "stepOut", resumedThread,
            TestContext.CancellationToken).ConfigureAwait(false);
        await AssertAsyncStepOutFrameAsync(client, callerThread, sourcePath, callerLine,
            "Consume" + kind + "Async", "42").ConfigureAwait(false);

        int continueSequence = await client.SendRequestAsync("continue", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        await ReadSuccessfulTerminationAsync(client, continueSequence, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    private async Task AssertAsyncStepOutFrameAsync(DapTestClient client, int threadId, string sourcePath,
        int line, string method, string answer)
    {
        JsonElement frame = await ReadTopSourceFrameAsync(client, threadId).ConfigureAwait(false);
        Assert.AreEqual("Csls.TestProcessHost.DebuggerAsyncStepOutFixture." + method,
            frame.GetProperty("name").GetString(), frame.GetRawText());
        Assert.AreEqual(line, frame.GetProperty("line").GetInt32());
        Assert.IsTrue(DebuggerTestPath.AreEquivalent(sourcePath, frame.GetProperty("source").GetProperty("path").GetString()));
        int frameId = frame.GetProperty("id").GetInt32();
        JsonElement evaluated = await ReadEvaluationAsync(client, frameId, "answer", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(answer, evaluated.GetProperty("result").GetString());
        (_, int localsReference) = await ReadFrameScopeReferencesAsync(client, frameId).ConfigureAwait(false);
        JsonElement[] locals = await ReadVariablesAsync(client, localsReference).ConfigureAwait(false);
        JsonElement local = Assert.ContainsSingle(locals.Where(item => item.GetProperty("name").GetString() == "answer"));
        Assert.AreEqual(answer, local.GetProperty("value").GetString());
        Assert.AreEqual("answer", local.GetProperty("evaluateName").GetString());
    }
}
