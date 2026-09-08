using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies physical static storage through real stopped threads and runtime initialization.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Reads and mutates distinct thread-static slots using each selected frame's thread identity.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ThreadStaticFieldsUseSelectedFrame()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
        (int threadId, int frameId) = await LaunchStaticStorageAsync(client).ConfigureAwait(false);
        (int workerThreadId, int workerFrameId) = await ReadStaticStorageWorkerFrameAsync(client).ConfigureAwait(false);
        Assert.AreNotEqual(threadId, workerThreadId);
        Assert.AreNotEqual(frameId, workerFrameId);
        const string Expression = "Csls.Debugger.Fixtures.CSharp.DebuggerStaticStorageFixture.s_threadNumber";
        await AssertStructAssignmentEvaluationAsync(client, frameId, Expression, "11", "int").ConfigureAwait(false);
        await AssertStructAssignmentEvaluationAsync(client, workerFrameId, Expression, "22", "int").ConfigureAwait(false);
        JsonElement mainWrite = await ReadSetExpressionAsync(client, frameId, Expression, "73", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("73", mainWrite.GetProperty("value").GetString());
        await AssertStructAssignmentEvaluationAsync(client, workerFrameId, Expression, "22", "int").ConfigureAwait(false);
        JsonElement workerWrite = await ReadSetExpressionAsync(client, workerFrameId, Expression, "82", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("82", workerWrite.GetProperty("value").GetString());
        await AssertStructAssignmentEvaluationAsync(client, frameId, Expression, "73", "int").ConfigureAwait(false);
        await AssertStructAssignmentEvaluationAsync(client, workerFrameId, Expression, "82", "int").ConfigureAwait(false);
        Assert.AreEqual((workerThreadId, workerFrameId), await ReadStaticStorageWorkerFrameAsync(client).ConfigureAwait(false));
        await AssertStructAssignmentEvaluationAsync(client, frameId, "sentinel", "42", "int").ConfigureAwait(false);
        await ContinueEntryToExitAsync(client, threadId, "--static-storage73:82:0").ConfigureAwait(false);
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    /// <summary>
    /// Leaves an uninitialized type untouched until an explicit target call initializes its storage once.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StaticInspectionPreservesTypeInitialization()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
        (int threadId, int frameId) = await LaunchStaticStorageAsync(client).ConfigureAwait(false);
        const string Counter = "Csls.Debugger.Fixtures.CSharp.DebuggerStaticStorageFixture.s_initializations";
        const string UntouchedType = "Csls.Debugger.Fixtures.CSharp.DebuggerUninitializedStaticStorage";
        await AssertStructAssignmentEvaluationAsync(client, frameId, Counter, "0", "int").ConfigureAwait(false);
        JsonElement unavailable = await ReadEvaluationAsync(client, frameId, $"{UntouchedType}.s_number", success: false,
            TestContext.CancellationToken).ConfigureAwait(false);
        string? message = unavailable.GetProperty("message").GetString();
        Assert.AreEqual($"Static storage for '{UntouchedType}' has not been initialized in the selected thread.", message);
        await AssertStructAssignmentEvaluationAsync(client, frameId, Counter, "0", "int").ConfigureAwait(false);
        await AssertStructAssignmentEvaluationAsync(client, frameId, "sentinel", "42", "int").ConfigureAwait(false);
        await AssertStructAssignmentEvaluationAsync(client, frameId, $"{UntouchedType}.ReadNumber()", "314", "int").ConfigureAwait(false);
        await AssertAssignmentInvalidationAsync(client, targetCodeExecuted: true, TestContext.CancellationToken).ConfigureAwait(false);
        JsonElement currentFrame = await ReadTopSourceFrameAsync(client, threadId).ConfigureAwait(false);
        int currentFrameId = currentFrame.GetProperty("id").GetInt32();
        await AssertStructAssignmentEvaluationAsync(client, currentFrameId, Counter, "1", "int").ConfigureAwait(false);
        await AssertStructAssignmentEvaluationAsync(client, currentFrameId, $"{UntouchedType}.s_number", "314", "int").ConfigureAwait(false);
        await ContinueEntryToExitAsync(client, threadId, "--static-storage11:22:1").ConfigureAwait(false);
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    private async Task<(int ThreadId, int FrameId)> LaunchStaticStorageAsync(DapTestClient client)
    {
        string source = Path.Join(FindRepositoryRoot(), "test-assets", "Csls.Debugger.Fixtures.CSharp", "DebuggerStaticStorageFixture.cs");
        int line = FindSourceLine(await File.ReadAllLinesAsync(source, TestContext.CancellationToken).ConfigureAwait(false),
            "Console.Write(arguments[0]);");
        int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, initialize, "initialize", success: true);
        }
        _ = await client.SendRequestAsync("launch", writer => WriteLaunchArguments(writer,
            DebuggerLanguageFixtures.GetProgramPath("Csls.Debugger.Fixtures.CSharp", "Debug"), ["--static-storage"],
            wait: true, noDebug: false, suppressJitOptimizations: true), TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertEvent(initialized.RootElement, "initialized");
        }
        int threadId = await ConfigureBreakpointAsync(client, source, line).ConfigureAwait(false);
        return (threadId, await AssertStoppedFrameAsync(client, threadId, source, line).ConfigureAwait(false));
    }

    private async Task<(int ThreadId, int FrameId)> ReadStaticStorageWorkerFrameAsync(DapTestClient client)
    {
        int threadsSequence = await client.SendRequestAsync("threads", WriteEmptyObject, TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument threads = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(threads.RootElement, threadsSequence, "threads", success: true);
        foreach (int threadId in threads.RootElement.GetProperty("body").GetProperty("threads").EnumerateArray()
            .Select(static thread => thread.GetProperty("id").GetInt32()))
        {
            int stack = await client.SendRequestAsync("stackTrace", writer => WriteStackArguments(writer, threadId),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertResponse(response.RootElement, stack, "stackTrace", success: true);
            JsonElement frame = response.RootElement.GetProperty("body").GetProperty("stackFrames").EnumerateArray()
                .FirstOrDefault(static frame => frame.GetProperty("name").GetString() is string name &&
                    name.Contains("HoldWorker", StringComparison.Ordinal));
            if (frame.ValueKind != JsonValueKind.Undefined)
            {
                return (threadId, frame.GetProperty("id").GetInt32());
            }
        }
        Assert.Fail($"The stopped worker frame is missing.{Environment.NewLine}{client.ProtocolTranscript}");
        return default;
    }
}
