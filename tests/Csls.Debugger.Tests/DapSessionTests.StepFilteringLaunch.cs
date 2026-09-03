using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Coordinates DAP launch and completion for property step-filtering probes.
/// </summary>
public sealed partial class DapSessionTests
{
    private async Task<int> StartStepFilteringTargetAsync(
        DapTestClient client,
        string sourcePath,
        int breakpointLine,
        string waitPath,
        bool enableStepFiltering)
    {
        int initializeSequence = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);
        int launchSequence = await client.SendRequestAsync(
            "launch",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("program", ResolveTestProcessHost());
                writer.WriteStartArray("args");
                writer.WriteStringValue("--debugger-step-filtering-fixture");
                writer.WriteStringValue(waitPath);
                writer.WriteEndArray();
                writer.WriteBoolean("enableStepFiltering", enableStepFiltering);
                WriteDefaultSourceFileMap(writer);
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");
        int breakpointsSequence = await client.SendRequestAsync(
            "setBreakpoints",
            writer => WriteSourceBreakpointArguments(writer, sourcePath, breakpointLine),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument breakpoints = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(
            breakpoints.RootElement,
            breakpointsSequence,
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

    private async Task CompleteStepFilteringTargetAsync(
        DapTestClient client,
        string waitPath)
    {
        await File.WriteAllTextAsync(
            waitPath,
            string.Empty,
            TestContext.CancellationToken).ConfigureAwait(false);
        int sequence = await client.SendRequestAsync(
            "continue",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        await ReadSuccessfulTerminationAsync(
            client,
            sequence,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
    }
}
