using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies ValueTask resumption and async finally breakpoints through real pipe coordination.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Follows an incomplete ValueTask onto its resumption thread and stops inside its finally block.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StepOverIncompleteValueTaskStopsAfterResumptionAndInFinally()
    {
        string sourcePath = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "DebuggerValueTaskStepFixture.cs");
        string[] sourceLines = await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken).ConfigureAwait(false);
        int awaitLine = FindSourceLine(sourceLines, "received = await pipe.ReadAsync");
        int resumedLine = FindSourceLine(sourceLines, "answer += scopedIncrement * buffer[0];");
        int finallyLine = FindSourceLine(sourceLines, "answer++;");
        string pipeName = $"cv-{Guid.NewGuid():N}";
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Task connection = pipe.WaitForConnectionAsync(TestContext.CancellationToken);
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        int initialThread = await LaunchToSourceBreakpointAsync(client, sourcePath, awaitLine,
            ["--debugger-valuetask-step-fixture", pipeName]).ConfigureAwait(false);
        await connection.ConfigureAwait(false);
        await AssertValueTaskFrameAsync(client, initialThread, sourcePath, awaitLine, "40").ConfigureAwait(false);

        Task<int> stepping = StepAndReadStopAsync(client, "next", initialThread, TestContext.CancellationToken);
        byte[] suspended = new byte[1];
        await pipe.ReadExactlyAsync(suspended, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual((byte)1, suspended[0]);
        await pipe.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
        int resumedThread = await stepping.ConfigureAwait(false);
        Assert.AreNotEqual(initialThread, resumedThread);
        await AssertValueTaskFrameAsync(client, resumedThread, sourcePath, resumedLine, "40").ConfigureAwait(false);

        _ = await AssertSourcePolicyBreakpointAsync(client, sourcePath, finallyLine, verified: true).ConfigureAwait(false);
        int finallyThread = await ContinueEntryToUserBreakpointAsync(client).ConfigureAwait(false);
        Assert.AreEqual(resumedThread, finallyThread);
        await AssertValueTaskFrameAsync(client, finallyThread, sourcePath, finallyLine, "41", scopedLocalIsActive: false).ConfigureAwait(false);

        int continueSequence = await client.SendRequestAsync("continue", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        await ReadSuccessfulTerminationAsync(client, continueSequence, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    private async Task AssertValueTaskFrameAsync(DapTestClient client, int threadId, string sourcePath, int line, string answer,
        bool scopedLocalIsActive = true)
    {
        JsonElement frame = await ReadTopSourceFrameAsync(client, threadId).ConfigureAwait(false);
        Assert.AreEqual("Csls.TestProcessHost.DebuggerValueTaskStepFixture.ReadAndIncrementAsync", frame.GetProperty("name").GetString());
        Assert.AreEqual(line, frame.GetProperty("line").GetInt32());
        Assert.IsTrue(DebuggerTestPath.AreEquivalent(sourcePath, frame.GetProperty("source").GetProperty("path").GetString()));
        JsonElement value = await ReadEvaluationAsync(client, frame.GetProperty("id").GetInt32(), "answer",
            success: true, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(answer, value.GetProperty("result").GetString());
        (_, int localsReference) = await ReadFrameScopeReferencesAsync(client, frame.GetProperty("id").GetInt32()).ConfigureAwait(false);
        JsonElement[] locals = await ReadVariablesAsync(client, localsReference).ConfigureAwait(false);
        JsonElement local = Assert.ContainsSingle(locals.Where(item => item.GetProperty("name").GetString() == "answer"));
        Assert.AreEqual(answer, local.GetProperty("value").GetString());
        Assert.AreEqual("int", local.GetProperty("type").GetString());
        Assert.AreEqual("answer", local.GetProperty("evaluateName").GetString());
        JsonElement[] scopedLocals = [.. locals.Where(item => item.GetProperty("name").GetString() == "scopedIncrement")];
        JsonElement scopedValue = await ReadEvaluationAsync(client, frame.GetProperty("id").GetInt32(), "scopedIncrement",
            success: scopedLocalIsActive, TestContext.CancellationToken).ConfigureAwait(false);
        if (scopedLocalIsActive)
        {
            Assert.AreEqual("1", Assert.ContainsSingle(scopedLocals).GetProperty("value").GetString());
            Assert.AreEqual("1", scopedValue.GetProperty("result").GetString());
        }
        else
        {
            Assert.IsEmpty(scopedLocals);
            Assert.Contains("unavailable", scopedValue.GetProperty("message").GetString()!);
        }
    }
}
