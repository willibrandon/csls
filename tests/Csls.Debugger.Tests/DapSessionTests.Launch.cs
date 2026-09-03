using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies launched debugger target activation and output forwarding.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Launches a real managed process after configuration and forwards its output and exit.
    /// </summary>
    [TestMethod]
    public async Task NoDebugLaunchRunsOwnedProcessAfterConfiguration()
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
        JsonElement capabilities = initialize.RootElement.GetProperty("body");
        Assert.IsTrue(capabilities.GetProperty("supportsConfigurationDoneRequest").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsModulesRequest").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsLoadedSourcesRequest").GetBoolean());
        Assert.IsTrue(
            capabilities.GetProperty("supportsBreakpointLocationsRequest").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsFunctionBreakpoints").GetBoolean());
        Assert.HasCount(
            3,
            capabilities.GetProperty("exceptionBreakpointFilters").EnumerateArray().ToArray());
        Assert.IsTrue(capabilities.GetProperty("supportsExceptionInfoRequest").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsVariablePaging").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsReadMemoryRequest").GetBoolean());
        Assert.HasCount(11, capabilities.EnumerateObject().ToArray());

        string processHost = ResolveTestProcessHost();
        int launchSequence = await client.SendRequestAsync(
            "launch",
            writer => WriteLaunchArguments(
                writer,
                processHost,
                ["--print-environment", "CSLS_DEBUGGER_TEST_VALUE"],
                wait: false),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");

        int configurationSequence = await client.SendRequestAsync(
            "configurationDone",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument configuration = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(
            configuration.RootElement,
            configurationSequence,
            "configurationDone",
            success: true);
        using JsonDocument launch = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(launch.RootElement, launchSequence, "launch", success: true);
        using JsonDocument process = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(process.RootElement, "process");
        Assert.IsGreaterThan(0, process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32());

        using JsonDocument output = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(output.RootElement, "output");
        Assert.AreEqual("stdout", output.RootElement.GetProperty("body").GetProperty("category").GetString());
        Assert.AreEqual(
            "transport-value",
            output.RootElement.GetProperty("body").GetProperty("output").GetString());
        using JsonDocument exited = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(exited.RootElement, "exited");
        Assert.AreEqual(0, exited.RootElement.GetProperty("body").GetProperty("exitCode").GetInt32());
        using JsonDocument terminated = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(terminated.RootElement, "terminated");
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    /// <summary>
    /// Launches a real managed assembly through dbgshim and preserves DAP protocol output.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedLaunchActivatesCoreClrAndForwardsTargetOutput()
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
        int launchSequence = await client.SendRequestAsync(
            "launch",
            writer => WriteLaunchArguments(
                writer,
                ResolveTestProcessHost(),
                ["--print-environment-and-exit", "CSLS_DEBUGGER_TEST_VALUE", "23"],
                wait: false,
                noDebug: false),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");

        int configurationSequence = await client.SendRequestAsync(
            "configurationDone",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument configuration = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(
            configuration.RootElement,
            configurationSequence,
            "configurationDone",
            success: true);
        using JsonDocument launch = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(launch.RootElement, launchSequence, "launch", success: true);
        using JsonDocument process = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(process.RootElement, "process");
        Assert.IsGreaterThan(
            0,
            process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32());

        using JsonDocument output = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(output.RootElement, "output");
        Assert.AreEqual("stdout", output.RootElement.GetProperty("body").GetProperty("category").GetString());
        Assert.AreEqual(
            "transport-value",
            output.RootElement.GetProperty("body").GetProperty("output").GetString());
        using JsonDocument exited = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(exited.RootElement, "exited");
        Assert.AreEqual(23, exited.RootElement.GetProperty("body").GetProperty("exitCode").GetInt32());
        using JsonDocument terminated = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(terminated.RootElement, "terminated");
        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

}
