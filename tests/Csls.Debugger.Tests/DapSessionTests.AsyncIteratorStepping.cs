using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies source stepping between suspended async iterators and await-foreach consumers.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Stops at the next authored statement even when the current method contains a later await.
    /// </summary>
    /// <param name="configuration">The compiler optimization configuration.</param>
    [TestMethod]
    [DataRow("Debug")]
    [DataRow("Release")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StepBeforeAsyncIteratorAwaitStopsAtNextStatement(string configuration)
    {
        string sourcePath = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "DebuggerAsyncIteratorStepFixture.cs");
        string[] lines = await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken).ConfigureAwait(false);
        int initialLine = FindSourceLine(lines, "int value = 40;");
        int nextLine = FindSourceLine(lines, "byte[] buffer = new byte[1];");
        string pipeName = $"ca-{Guid.NewGuid():N}";
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Task connection = pipe.WaitForConnectionAsync(TestContext.CancellationToken);
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        using DapTestCancellationCapture cancellationLog = CaptureProtocolOnCancellation(client);
        int initialThread = await LaunchToSourceBreakpointAsync(client, sourcePath, initialLine,
            ["--debugger-async-iterator-step-fixture", pipeName], ResolveAsyncIteratorProgram(configuration),
            suppressJitOptimizations: true).ConfigureAwait(false);
        await connection.ConfigureAwait(false);
        byte[] suspended = new byte[1];
        Task acknowledgement = pipe.ReadExactlyAsync(suspended, TestContext.CancellationToken).AsTask();
        Task<int> stepping = StepAndReadStopAsync(client, "next", initialThread, TestContext.CancellationToken);
        Task first = await Task.WhenAny(stepping, acknowledgement).ConfigureAwait(false);
        if (first == acknowledgement)
        {
            await acknowledgement.ConfigureAwait(false);
            await pipe.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
        }

        int nextThread = await stepping.ConfigureAwait(false);
        await AssertAsyncIteratorFrameAsync(client, nextThread, sourcePath, nextLine,
            "ReadAndEnumerateAsync", "value", "40").ConfigureAwait(false);
        Assert.AreEqual(initialThread, nextThread);
        int continueSequence = await client.SendRequestAsync("continue", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        await acknowledgement.ConfigureAwait(false);
        Assert.AreEqual((byte)1, suspended[0]);
        await pipe.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
        await ReadSuccessfulTerminationAsync(client, continueSequence, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    /// <summary>
    /// Follows an async iterator's resumption and then its yielded value into the consumer.
    /// </summary>
    /// <param name="configuration">The compiler optimization configuration.</param>
    [TestMethod]
    [DataRow("Debug")]
    [DataRow("Release")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StepAcrossAsyncIteratorAwaitAndYieldPreservesSourceLocals(string configuration)
    {
        string sourcePath = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "DebuggerAsyncIteratorStepFixture.cs");
        string[] lines = await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken).ConfigureAwait(false);
        int awaitLine = FindSourceLine(lines, "await pipe.ReadExactlyAsync");
        int resumedLine = FindSourceLine(lines, "value += buffer[0];");
        int yieldLine = FindSourceLine(lines, "yield return CollectAndReturn");
        int consumerLine = FindSourceLine(lines, "await foreach");
        string pipeName = $"ci-{Guid.NewGuid():N}";
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Task connection = pipe.WaitForConnectionAsync(TestContext.CancellationToken);
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        using DapTestCancellationCapture cancellationLog = CaptureProtocolOnCancellation(client);
        int initialThread = await LaunchToSourceBreakpointAsync(client, sourcePath, awaitLine,
            ["--debugger-async-iterator-step-fixture", pipeName], ResolveAsyncIteratorProgram(configuration),
            suppressJitOptimizations: true).ConfigureAwait(false);
        await connection.ConfigureAwait(false);
        await AssertAsyncIteratorFrameAsync(client, initialThread, sourcePath, awaitLine,
            "ReadAndEnumerateAsync", "value", "40").ConfigureAwait(false);

        Task<int> stepping = StepAndReadStopAsync(client, "next", initialThread, TestContext.CancellationToken);
        byte[] suspended = new byte[1];
        await pipe.ReadExactlyAsync(suspended, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual((byte)1, suspended[0]);
        await pipe.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
        int resumedThread = await stepping.ConfigureAwait(false);
        Assert.AreNotEqual(initialThread, resumedThread);
        await AssertAsyncIteratorFrameAsync(client, resumedThread, sourcePath, resumedLine,
            "ReadAndEnumerateAsync", "value", "40").ConfigureAwait(false);

        _ = await AssertSourcePolicyBreakpointAsync(client, sourcePath, yieldLine, verified: true).ConfigureAwait(false);
        int yieldThread = await ContinueEntryToUserBreakpointAsync(client).ConfigureAwait(false);
        await AssertAsyncIteratorFrameAsync(client, yieldThread, sourcePath, yieldLine,
            "ReadAndEnumerateAsync", "value", "41").ConfigureAwait(false);
        await ClearSourceBreakpointsAsync(client, sourcePath).ConfigureAwait(false);
        int consumerThread = await StepAndReadStopAsync(client, "next", yieldThread, TestContext.CancellationToken).ConfigureAwait(false);
        await AssertAsyncIteratorFrameAsync(client, consumerThread, sourcePath, consumerLine,
            "ConsumeAsync", "total", "0").ConfigureAwait(false);

        int secondYieldLine = FindSourceLine(lines, "yield return checked(value);");
        _ = await AssertSourcePolicyBreakpointAsync(client, sourcePath, secondYieldLine, verified: true).ConfigureAwait(false);
        int secondYieldThread = await ContinueEntryToUserBreakpointAsync(client).ConfigureAwait(false);
        await AssertAsyncIteratorFrameAsync(client, secondYieldThread, sourcePath, secondYieldLine,
            "ReadAndEnumerateAsync", "value", "42").ConfigureAwait(false);
        await ClearSourceBreakpointsAsync(client, sourcePath).ConfigureAwait(false);
        int secondConsumerThread = await StepAndReadStopAsync(client, "next", secondYieldThread,
            TestContext.CancellationToken).ConfigureAwait(false);
        await AssertAsyncIteratorFrameAsync(client, secondConsumerThread, sourcePath, consumerLine,
            "ConsumeAsync", "total", "41").ConfigureAwait(false);

        int continueSequence = await client.SendRequestAsync("continue", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        await ReadSuccessfulTerminationAsync(client, continueSequence, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    private static string ResolveAsyncIteratorProgram(string configuration) =>
        Path.Join(FindRepositoryRoot(), "artifacts", "bin", "Csls.TestProcessHost",
            configuration switch
            {
                "Debug" => "debug",
                "Release" => "release",
                _ => throw new ArgumentOutOfRangeException(nameof(configuration))
            }, "csls-test-process-host.dll");

    private async Task AssertAsyncIteratorFrameAsync(DapTestClient client, int threadId, string sourcePath, int line,
        string method, string localName, string localValue)
    {
        JsonElement frame = await ReadTopSourceFrameAsync(client, threadId).ConfigureAwait(false);
        if (frame.GetProperty("name").GetString() != "Csls.TestProcessHost.DebuggerAsyncIteratorStepFixture." + method)
        {
            int modulesSequence = await client.SendRequestAsync("modules", WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument modules = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertResponse(modules.RootElement, modulesSequence, "modules", success: true);
            TestContext.WriteLine($"Module policy at unexpected step stop: {modules.RootElement}");
        }

        Assert.AreEqual("Csls.TestProcessHost.DebuggerAsyncIteratorStepFixture." + method,
            frame.GetProperty("name").GetString(), frame.GetRawText());
        Assert.AreEqual(line, frame.GetProperty("line").GetInt32());
        Assert.IsTrue(DebuggerTestPath.AreEquivalent(sourcePath, frame.GetProperty("source").GetProperty("path").GetString()));
        int frameId = frame.GetProperty("id").GetInt32();
        JsonElement value = await ReadEvaluationAsync(client, frameId, localName, success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(localValue, value.GetProperty("result").GetString());
        (_, int localsReference) = await ReadFrameScopeReferencesAsync(client, frameId).ConfigureAwait(false);
        JsonElement[] locals = await ReadVariablesAsync(client, localsReference).ConfigureAwait(false);
        JsonElement local = Assert.ContainsSingle(locals.Where(item => item.GetProperty("name").GetString() == localName));
        Assert.AreEqual(localValue, local.GetProperty("value").GetString());
        Assert.AreEqual("int", local.GetProperty("type").GetString());
        Assert.AreEqual(localName, local.GetProperty("evaluateName").GetString());
    }
}
