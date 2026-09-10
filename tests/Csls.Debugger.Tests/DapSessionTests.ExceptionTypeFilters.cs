using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies exact and inherited managed exception type filters through real DAP targets.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Skips a nonmatching exception and stops on the configured exact type.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task ExactExceptionTypeFilterStopsOnMatchingException() =>
        ExerciseExceptionTypeFilterAsync(
            "System.InvalidOperationException",
            "System.InvalidOperationException");

    /// <summary>
    /// Stops when a configured base exception type matches the thrown type hierarchy.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task BaseExceptionTypeFilterStopsOnDerivedException() =>
        ExerciseExceptionTypeFilterAsync("System.Exception", "System.ArgumentException");

    private async Task ExerciseExceptionTypeFilterAsync(
        string condition,
        string expectedExceptionId)
    {
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-exception-filter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            await RunExceptionTypeFilterAsync(
                testDirectory,
                condition,
                expectedExceptionId).ConfigureAwait(false);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(
                testDirectory,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task RunExceptionTypeFilterAsync(
        string testDirectory,
        string condition,
        string expectedExceptionId)
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
        AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);
        Assert.IsTrue(initialize.RootElement.GetProperty("body")
            .GetProperty("supportsExceptionFilterOptions")
            .GetBoolean());
        int launchSequence = await client.SendRequestAsync(
            "launch",
            writer => WriteLaunchArguments(
                writer,
                ResolveTestProcessHost(),
                [
                    "--debugger-exception-filter-fixture",
                    Path.Join(testDirectory, "continue.signal")
                ],
                wait: true,
                noDebug: false),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");
        int exceptionsSequence = await client.SendRequestAsync(
            "setExceptionBreakpoints",
            writer => WriteExceptionTypeFilter(writer, condition),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument exceptions = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(
            exceptions.RootElement,
            exceptionsSequence,
            "setExceptionBreakpoints",
            success: true);
        int configurationSequence = await client.SendRequestAsync(
            "configurationDone",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        int threadId = await ReadExceptionStopAsync(
            client,
            launchSequence,
            configurationSequence).ConfigureAwait(false);
        await AssertExceptionTypeAsync(client, threadId, expectedExceptionId)
            .ConfigureAwait(false);
        await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    private async Task AssertExceptionTypeAsync(
        DapTestClient client,
        int threadId,
        string expectedExceptionId)
    {
        int sequence = await client.SendRequestAsync(
            "exceptionInfo",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("threadId", threadId);
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "exceptionInfo", success: true);
        Assert.AreEqual(
            expectedExceptionId,
            response.RootElement.GetProperty("body").GetProperty("exceptionId").GetString());
    }

    private static void WriteExceptionTypeFilter(Utf8JsonWriter writer, string condition)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("filters");
        writer.WriteEndArray();
        writer.WriteStartArray("filterOptions");
        writer.WriteStartObject();
        writer.WriteString("filterId", "all");
        writer.WriteString("condition", condition);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
