using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies source-aware stepping and safe instruction-pointer movement against CoreCLR.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Selects the second call on a line and moves the active frame to an approved statement.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StepTargetAndGotoUseRuntimeApprovedManagedLocations()
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourcePath = Path.Join(
            repositoryRoot,
            "tests",
            "Csls.TestProcessHost",
            "DebuggerStepFixture.cs");
        string[] lines = await File.ReadAllLinesAsync(
            sourcePath,
            TestContext.CancellationToken).ConfigureAwait(false);
        int callLine = FindSourceLine(lines, "int combined = AddTwo(seed - 1) + AddTwo(seed);");
        int seedLine = FindSourceLine(lines, "int seed = 40;");
        int calleeLine = FindSourceLine(lines, "private static int AddTwo(int value)") + 1;
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-step-target-{Guid.NewGuid():N}.signal");
        try
        {
            (DapTestClient client, int threadId) = await StartStepTargetFixtureAsync(
                sourcePath,
                callLine,
                waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement caller = await ReadTopSourceFrameAsync(client, threadId)
                .ConfigureAwait(false);
            int gotoSeed = await ReadGotoTargetAsync(
                client,
                sourcePath,
                seedLine).ConfigureAwait(false);
            await GotoAndAssertOrderAsync(client, threadId, gotoSeed).ConfigureAwait(false);
            JsonElement seed = await ReadTopSourceFrameAsync(client, threadId)
                .ConfigureAwait(false);
            Assert.AreEqual(seedLine, seed.GetProperty("line").GetInt32());
            await AssertGotoTargetIsStaleAsync(client, threadId, gotoSeed).ConfigureAwait(false);

            int gotoCall = await ReadGotoTargetAsync(
                client,
                sourcePath,
                callLine).ConfigureAwait(false);
            await GotoAndAssertOrderAsync(client, threadId, gotoCall).ConfigureAwait(false);
            caller = await ReadTopSourceFrameAsync(client, threadId).ConfigureAwait(false);
            Assert.AreEqual(callLine, caller.GetProperty("line").GetInt32());
            int frameId = caller.GetProperty("id").GetInt32();

            int targetsSequence = await client.SendRequestAsync(
                "stepInTargets",
                writer => WriteFrameId(writer, frameId),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument targetResponse = await client
                .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertResponse(
                targetResponse.RootElement,
                targetsSequence,
                "stepInTargets",
                success: true);
            JsonElement[] targets = [.. targetResponse.RootElement.GetProperty("body")
                .GetProperty("targets").EnumerateArray()];
            Assert.HasCount(2, targets);
            Assert.HasCount(2, targets.Where(target => target.GetProperty("label").GetString()!
                .EndsWith(".AddTwo", StringComparison.Ordinal)));
            JsonElement addTwo = targets[1];
            int stepTargetId = addTwo.GetProperty("id").GetInt32();

            threadId = await TargetedStepAndReadStopAsync(
                client,
                threadId,
                stepTargetId).ConfigureAwait(false);
            JsonElement callee = await ReadTopSourceFrameAsync(client, threadId)
                .ConfigureAwait(false);
            Assert.AreEqual(
                "Csls.TestProcessHost.DebuggerStepFixture.AddTwo",
                callee.GetProperty("name").GetString(),
                $"Stopped at source line {callee.GetProperty("line").GetInt32()}.");
            Assert.AreEqual(calleeLine, callee.GetProperty("line").GetInt32());
            await AssertStepTargetIsStaleAsync(client, threadId, stepTargetId)
                .ConfigureAwait(false);

            threadId = await StepAndReadStopAsync(
                client,
                "stepOut",
                threadId,
                TestContext.CancellationToken).ConfigureAwait(false);
            _ = await ReadTopSourceFrameAsync(client, threadId).ConfigureAwait(false);

            await ClearSourceBreakpointsAsync(client, sourcePath).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                waitPath,
                string.Empty,
                TestContext.CancellationToken).ConfigureAwait(false);
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
        finally
        {
            File.Delete(waitPath);
        }
    }

    private async Task<(DapTestClient Client, int ThreadId)> StartStepTargetFixtureAsync(
        string sourcePath,
        int breakpointLine,
        string waitPath)
    {
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsTrue(initialize.RootElement.GetProperty("body")
            .GetProperty("supportsInstructionBreakpoints").GetBoolean());
        int launchSequence = await client.SendRequestAsync(
            "launch",
            writer => WriteLaunchArguments(
                writer,
                ResolveTestProcessHost(),
                ["--debugger-step-fixture", waitPath],
                wait: true,
                noDebug: false),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await ClearInstructionBreakpointsAsync(client).ConfigureAwait(false);
        int breakpointsSequence = await client.SendRequestAsync(
            "setBreakpoints",
            writer => WriteSourceBreakpointArguments(writer, sourcePath, breakpointLine),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument breakpoints = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(
            breakpoints.RootElement,
            breakpointsSequence,
            "setBreakpoints",
            success: true);
        int configurationSequence = await client.SendRequestAsync(
            "configurationDone",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        int threadId = await ReadInitialBreakpointStopAsync(
            client,
            configurationSequence,
            launchSequence,
            TestContext.CancellationToken).ConfigureAwait(false);
        return (client, threadId);
    }
}
