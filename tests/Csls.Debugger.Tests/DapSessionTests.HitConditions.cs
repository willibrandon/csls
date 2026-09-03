using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies managed source and function hit-count breakpoints through real DAP targets.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Stops a repeated source breakpoint on its exact requested hit.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task SourceBreakpointHonorsExactHitCondition() =>
        ExerciseHitConditionAsync(
            useFunctionBreakpoint: false,
            hitCondition: "3",
            expectedProgress: "3");

    /// <summary>
    /// Stops a repeated function breakpoint on its exact requested hit.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task FunctionBreakpointHonorsExactHitCondition() =>
        ExerciseHitConditionAsync(
            useFunctionBreakpoint: true,
            hitCondition: "3",
            expectedProgress: "3");

    /// <summary>
    /// Stops a repeated source breakpoint on every requested multiple.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task SourceBreakpointHonorsMultipleHitCondition() =>
        ExerciseHitConditionAsync(
            useFunctionBreakpoint: false,
            hitCondition: "%2",
            expectedProgress: "2");

    /// <summary>
    /// Stops a repeated function breakpoint at the requested threshold.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task FunctionBreakpointHonorsThresholdHitCondition() =>
        ExerciseHitConditionAsync(
            useFunctionBreakpoint: true,
            hitCondition: ">=2",
            expectedProgress: "2");

    private async Task ExerciseHitConditionAsync(
        bool useFunctionBreakpoint,
        string hitCondition,
        string expectedProgress)
    {
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-hit-condition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        string signalPath = Path.Join(testDirectory, "continue.signal");
        string progressPath = Path.Join(testDirectory, "progress.txt");
        try
        {
            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
            int initializeSequence = await client.SendRequestAsync(
                "initialize",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialize = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                initialize.RootElement,
                initializeSequence,
                "initialize",
                success: true);
            Assert.IsTrue(initialize.RootElement.GetProperty("body")
                .GetProperty("supportsHitConditionalBreakpoints")
                .GetBoolean());
            int launchSequence = await client.SendRequestAsync(
                "launch",
                writer => WriteLaunchArguments(
                    writer,
                    ResolveTestProcessHost(),
                    ["--debugger-hit-fixture", signalPath, progressPath, "3"],
                    wait: true,
                    noDebug: false),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialized = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(initialized.RootElement, "initialized");
            int breakpointId = await SetHitConditionBreakpointAsync(
                client,
                useFunctionBreakpoint,
                hitCondition)
                .ConfigureAwait(false);
            int configurationSequence = await client.SendRequestAsync(
                "configurationDone",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            if (useFunctionBreakpoint)
            {
                _ = await ReadFunctionBreakpointStopAsync(
                    client,
                    configurationSequence,
                    launchSequence,
                    breakpointId).ConfigureAwait(false);
            }
            else
            {
                _ = await ReadInitialBreakpointStopAsync(
                    client,
                    configurationSequence,
                    launchSequence,
                    TestContext.CancellationToken).ConfigureAwait(false);
            }

            Assert.AreEqual(
                expectedProgress,
                await File.ReadAllTextAsync(
                    progressPath,
                    TestContext.CancellationToken).ConfigureAwait(false));
            await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                await client.WaitForExitAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    private async Task<int> SetHitConditionBreakpointAsync(
        DapTestClient client,
        bool useFunctionBreakpoint,
        string hitCondition)
    {
        string command = useFunctionBreakpoint
            ? "setFunctionBreakpoints"
            : "setBreakpoints";
        int sequence = await client.SendRequestAsync(
            command,
            writer => WriteHitConditionBreakpointArguments(
                writer,
                useFunctionBreakpoint,
                hitCondition),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, command, success: true);
        JsonElement breakpoint = response.RootElement.GetProperty("body")
            .GetProperty("breakpoints")[0];
        Assert.IsFalse(breakpoint.GetProperty("verified").GetBoolean());
        return breakpoint.GetProperty("id").GetInt32();
    }

}
