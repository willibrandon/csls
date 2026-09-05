using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies source stepping across real asynchronous suspension and resumption.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Steps over an incomplete await and stops in the resumed async method.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StepOverIncompleteAwaitStopsAfterResumption()
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourcePath = Path.Join(
            repositoryRoot,
            "tests",
            "Csls.TestProcessHost",
            "DebuggerAsyncStepFixture.cs");
        string[] sourceLines = await File.ReadAllLinesAsync(
            sourcePath,
            TestContext.CancellationToken).ConfigureAwait(false);
        int awaitLine = FindSourceLine(sourceLines, "await Task.Delay(250)");
        int resumedLine = FindSourceLine(sourceLines, "answer++;");

        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        int threadId = await LaunchToSourceBreakpointAsync(
            client,
            sourcePath,
            awaitLine,
            ["--debugger-async-step-fixture", "41"]).ConfigureAwait(false);
        (string initialName, string? initialPath, int initialLine) =
            await ReadSourceFrameAsync(
                client,
                threadId,
                sourcePath,
                TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("Csls.TestProcessHost.DebuggerAsyncStepFixture.RunAsync", initialName);
        Assert.IsTrue(DebuggerTestPath.AreEquivalent(sourcePath, initialPath));
        Assert.AreEqual(awaitLine, initialLine);

        int initialThreadId = threadId;
        threadId = await StepAndReadStopAsync(
            client,
            "next",
            threadId,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreNotEqual(initialThreadId, threadId);
        (string resumedName, string? resumedPath, int actualResumedLine) =
            await ReadSourceFrameAsync(
                client,
                threadId,
                sourcePath,
                TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("Csls.TestProcessHost.DebuggerAsyncStepFixture.RunAsync", resumedName);
        Assert.IsTrue(DebuggerTestPath.AreEquivalent(sourcePath, resumedPath));
        Assert.AreEqual(resumedLine, actualResumedLine);

        int continueSequence = await client.SendRequestAsync(
            "continue",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        await ReadSuccessfulTerminationAsync(
            client,
            continueSequence,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    /// <summary>
    /// Ignores a competing async instance that reaches the shared continuation first.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StepOverAwaitFollowsSelectedStateMachineInstance()
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourcePath = Path.Join(
            repositoryRoot,
            "tests",
            "Csls.TestProcessHost",
            "DebuggerAsyncStepFixture.cs");
        string[] sourceLines = await File.ReadAllLinesAsync(
            sourcePath,
            TestContext.CancellationToken).ConfigureAwait(false);
        int awaitLine = FindSourceLine(sourceLines, "await Task.Delay(delayMilliseconds)");
        int resumedLine = FindSourceLine(sourceLines, "value++;");

        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        int threadId = await LaunchToSourceBreakpointAsync(
            client,
            sourcePath,
            awaitLine,
            ["--debugger-concurrent-async-step-fixture"]).ConfigureAwait(false);
        await ClearSourceBreakpointsAsync(client, sourcePath).ConfigureAwait(false);
        threadId = await StepAndReadStopAsync(
            client,
            "next",
            threadId,
            TestContext.CancellationToken).ConfigureAwait(false);
        JsonElement frame = await ReadTopSourceFrameAsync(client, threadId).ConfigureAwait(false);
        Assert.AreEqual(
            "Csls.TestProcessHost.DebuggerAsyncStepFixture.DelayAndIncrementAsync",
            frame.GetProperty("name").GetString());
        Assert.AreEqual(resumedLine, frame.GetProperty("line").GetInt32());
        await AssertSelectedAsyncStateMachineAsync(
            client,
            frame.GetProperty("id").GetInt32()).ConfigureAwait(false);

        int continueSequence = await client.SendRequestAsync(
            "continue",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        await ReadSuccessfulTerminationAsync(
            client,
            continueSequence,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    private async Task<int> LaunchToSourceBreakpointAsync(
        DapTestClient client,
        string sourcePath,
        int breakpointLine,
        IReadOnlyList<string> arguments)
    {
        int initializeSequence = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);
        int launchSequence = await client.SendRequestAsync(
            "launch",
            writer => WriteLaunchArguments(
                writer,
                ResolveTestProcessHost(),
                arguments,
                wait: true,
                noDebug: false),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");
        int breakpointSequence = await client.SendRequestAsync(
            "setBreakpoints",
            writer => WriteSourceBreakpointArguments(writer, sourcePath, breakpointLine),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument breakpoints = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(
            breakpoints.RootElement,
            breakpointSequence,
            "setBreakpoints",
            success: true);
        int configurationSequence = await client.SendRequestAsync(
            "configurationDone",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        return await ReadInitialBreakpointStopAsync(
            client,
            configurationSequence,
            launchSequence,
            TestContext.CancellationToken).ConfigureAwait(false);
    }

    private async Task AssertSelectedAsyncStateMachineAsync(
        DapTestClient client,
        int frameId)
    {
        int sequence = await client.SendRequestAsync(
            "scopes",
            writer => WriteFrameId(writer, frameId),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "scopes", success: true);
        JsonElement arguments = response.RootElement.GetProperty("body").GetProperty("scopes")
            .EnumerateArray().Single(scope => scope.GetProperty("name").GetString() == "Arguments");
        JsonElement[] argumentValues = await ReadVariablesAsync(
            client,
            arguments.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
        Assert.HasCount(1, argumentValues);
        int stateMachineReference = argumentValues[0]
            .GetProperty("variablesReference").GetInt32();
        Assert.IsGreaterThan(0, stateMachineReference);
        JsonElement[] fields = await ReadVariablesAsync(client, stateMachineReference)
            .ConfigureAwait(false);
        JsonElement[] selectedValues = [.. fields.Where(field =>
            field.GetProperty("name").GetString() == "value" &&
            field.GetProperty("value").GetString() == "41")];
        Assert.HasCount(1, selectedValues);
    }
}
