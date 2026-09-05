using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies launched debugger target activation and output forwarding.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Preserves inherited loader settings when the adapter starts an application with or without debugging.
    /// </summary>
    /// <param name="variable">The inherited native loader environment variable.</param>
    /// <param name="noDebug">Whether to launch without attaching the managed debugger.</param>
    [TestMethod]
    [DataRow("LD_PRELOAD", false)]
    [DataRow("LD_PRELOAD", true)]
    [DataRow("LD_LIBRARY_PATH", false)]
    [DataRow("LD_LIBRARY_PATH", true)]
    [DataRow("DYLD_INSERT_LIBRARIES", false)]
    [DataRow("DYLD_INSERT_LIBRARIES", true)]
    [DataRow("DYLD_LIBRARY_PATH", false)]
    [DataRow("DYLD_LIBRARY_PATH", true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task LaunchPreservesInheritedLoaderEnvironment(string variable, bool noDebug) =>
        AssertLaunchedEnvironmentAsync(variable, noDebug, Environment.GetEnvironmentVariable(variable));

    /// <summary>
    /// Preserves explicit empty and populated parent loader settings through worker activation.
    /// </summary>
    /// <param name="variable">The inherited native loader search variable.</param>
    /// <param name="noDebug">Whether to launch without attaching the managed debugger.</param>
    /// <param name="empty">Whether the parent explicitly supplies an empty value.</param>
    [TestMethod]
    [DataRow("LD_LIBRARY_PATH", false, false)]
    [DataRow("LD_LIBRARY_PATH", true, false)]
    [DataRow("LD_LIBRARY_PATH", false, true)]
    [DataRow("LD_LIBRARY_PATH", true, true)]
    [DataRow("DYLD_LIBRARY_PATH", false, false)]
    [DataRow("DYLD_LIBRARY_PATH", true, false)]
    [DataRow("DYLD_LIBRARY_PATH", false, true)]
    [DataRow("DYLD_LIBRARY_PATH", true, true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task LaunchPreservesExplicitParentLoaderSetting(string variable, bool noDebug, bool empty)
    {
        string value = empty ? string.Empty : FindRepositoryRoot();
        return AssertLaunchedEnvironmentAsync(variable, noDebug, value,
            new Dictionary<string, string?> { [variable] = value });
    }

    /// <summary>
    /// Applies explicit target loader replacements and removals after restoring the caller's environment.
    /// </summary>
    /// <param name="variable">The inherited native loader search variable.</param>
    /// <param name="noDebug">Whether to launch without attaching the managed debugger.</param>
    /// <param name="remove">Whether the target removes the inherited setting.</param>
    [TestMethod]
    [DataRow("LD_LIBRARY_PATH", false, false)]
    [DataRow("LD_LIBRARY_PATH", true, false)]
    [DataRow("LD_LIBRARY_PATH", false, true)]
    [DataRow("LD_LIBRARY_PATH", true, true)]
    [DataRow("DYLD_LIBRARY_PATH", false, false)]
    [DataRow("DYLD_LIBRARY_PATH", true, false)]
    [DataRow("DYLD_LIBRARY_PATH", false, true)]
    [DataRow("DYLD_LIBRARY_PATH", true, true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task LaunchAppliesTargetLoaderOverrides(string variable, bool noDebug, bool remove)
    {
        string? value = remove ? null : Path.Join(FindRepositoryRoot(), "test-assets");
        return AssertLaunchedEnvironmentAsync(variable, noDebug, value,
            new Dictionary<string, string?> { [variable] = FindRepositoryRoot() },
            new Dictionary<string, string?> { [variable] = value });
    }

    /// <summary>
    /// Keeps the worker's saved native loader configuration out of the target process.
    /// </summary>
    /// <param name="noDebug">Whether to launch without attaching the managed debugger.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task LaunchKeepsWorkerLoaderSnapshotPrivate(bool noDebug) => AssertLaunchedEnvironmentAsync(
        "CSLS_DEBUGGER_INHERITED_LOADER_ENVIRONMENT", noDebug, expected: null);

    /// <summary>
    /// Preserves a caller-supplied preload even when it names the library also required by the worker.
    /// </summary>
    /// <param name="noDebug">Whether to launch without attaching the managed debugger.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task LaunchPreservesCallerPreloadedLibrary(bool noDebug)
    {
        (string variable, string library) = GetCallerPreload();
        Assert.IsTrue(File.Exists(library), $"The real preload library is missing: {library}");
        return AssertLaunchedEnvironmentAsync(variable, noDebug, library,
            new Dictionary<string, string?> { [variable] = library });
    }

    private static (string Variable, string Library) GetCallerPreload()
    {
        string worker = Environment.GetEnvironmentVariable("CSLS_DEBUGGER_WORKER_TEST_PATH")
            ?? Path.Join(FindRepositoryRoot(), "artifacts", "bin", "Csls.Debugger.Worker", "debug", "csls-debugger-worker.dll");
        string directory = Path.GetDirectoryName(Path.GetFullPath(worker))
            ?? throw new InvalidOperationException("The debugger worker has no containing directory.");
        return OperatingSystem.IsMacOS()
            ? ("DYLD_INSERT_LIBRARIES", Path.Join(directory, "Csls.Debugger.UnixWait.dylib"))
            : ("LD_PRELOAD", Path.Join(directory, "Csls.Debugger.UnixWait.so"));
    }

    private async Task AssertLaunchedEnvironmentAsync(
        string variable,
        bool noDebug,
        string? expected,
        IReadOnlyDictionary<string, string?>? parentEnvironment = null,
        IReadOnlyDictionary<string, string?>? targetEnvironment = null)
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken, parentEnvironment)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, initialize, "initialize", success: true);
        }

        int launch = await client.SendRequestAsync("launch", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("program", ResolveTestProcessHost());
            writer.WriteBoolean("noDebug", noDebug);
            writer.WriteStartArray("args");
            writer.WriteStringValue("--print-environment-entry");
            writer.WriteStringValue(variable);
            writer.WriteEndArray();
            WriteDefaultSourceFileMap(writer);
            if (targetEnvironment is not null)
            {
                writer.WriteStartObject("env");
                foreach ((string name, string? value) in targetEnvironment)
                {
                    writer.WriteString(name, value);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertEvent(initialized.RootElement, "initialized");
        }

        int configuration = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, configuration, "configurationDone", success: true);
        }

        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, launch, "launch", success: true);
        }

        using (JsonDocument process = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertEvent(process.RootElement, "process");
            Assert.IsGreaterThan(0, process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32());
        }

        var output = new System.Text.StringBuilder();
        bool exited = false;
        while (true)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement root = message.RootElement;
            string? eventName = root.GetProperty("event").GetString();
            if (eventName == "terminated")
            {
                Assert.IsTrue(exited);
                break;
            }

            if (eventName == "exited")
            {
                Assert.IsFalse(exited);
                Assert.AreEqual(23, root.GetProperty("body").GetProperty("exitCode").GetInt32());
                exited = true;
                continue;
            }

            AssertEvent(root, "output");
            Assert.AreEqual("stdout", root.GetProperty("body").GetProperty("category").GetString());
            _ = output.Append(root.GetProperty("body").GetProperty("output").GetString());
        }

        Assert.AreEqual(expected is null ? "unset" : $"set:{expected}", output.ToString(), variable);
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.IsEmpty(client.Diagnostics.ToString());
    }

    /// <summary>
    /// Rejects malformed entry-stop options before launching a target.
    /// </summary>
    [TestMethod]
    [DataRow("null")]
    [DataRow("\"true\"")]
    [DataRow("1")]
    [DataRow("{}")]
    [DataRow("[]")]
    public async Task LaunchRejectsInvalidStopAtEntry(string option)
    {
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        int sequence = await client.SendRequestAsync(
            "launch",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("program", ResolveTestProcessHost());
                writer.WritePropertyName("stopAtEntry");
                writer.WriteRawValue(option);
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "launch", success: false);
        Assert.Contains(
            "stopAtEntry",
            response.RootElement.GetProperty("message").GetString()!,
            StringComparison.Ordinal);
        await client.CloseProtocolAsync().ConfigureAwait(false);
        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    /// <summary>
    /// Launches a real managed process after configuration and forwards its output and exit.
    /// </summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NoDebugLaunchRunsOwnedProcessAfterConfiguration(bool? stopAtEntry)
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
        Assert.IsTrue(capabilities.GetProperty("supportsConditionalBreakpoints").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsLogPoints").GetBoolean());
        Assert.HasCount(
            3,
            capabilities.GetProperty("exceptionBreakpointFilters").EnumerateArray().ToArray());
        Assert.IsTrue(capabilities.GetProperty("supportsExceptionInfoRequest").GetBoolean());
        Assert.IsFalse(capabilities.TryGetProperty("supportsVariablePaging", out _));
        Assert.IsTrue(capabilities.GetProperty("supportsEvaluateForHovers").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsCompletionsRequest").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsSetVariable").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsSetExpression").GetBoolean());
        Assert.IsFalse(capabilities.TryGetProperty("supportsInvalidatedEvent", out _));
        Assert.IsTrue(capabilities.GetProperty("supportsCancelRequest").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsReadMemoryRequest").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsDisassembleRequest").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsInstructionBreakpoints").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsStepInTargetsRequest").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsGotoTargetsRequest").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsRestartRequest").GetBoolean());
        Assert.HasCount(22, capabilities.EnumerateObject().ToArray());

        string processHost = ResolveTestProcessHost();
        int launchSequence = await client.SendRequestAsync(
            "launch",
            writer => WriteLaunchArguments(
                writer,
                processHost,
                ["--print-environment", "CSLS_DEBUGGER_TEST_VALUE"],
                wait: false,
                stopAtEntry: stopAtEntry),
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
        JsonElement processBody = process.RootElement.GetProperty("body");
        Assert.IsGreaterThan(0, processBody.GetProperty("systemProcessId").GetInt32());
        string runtimeHost = Environment.GetEnvironmentVariable("CSLS_RUNTIME_HOST_PATH") ??
            "dotnet";
        Assert.AreEqual(
            Path.GetFileNameWithoutExtension(runtimeHost),
            processBody.GetProperty("name").GetString());

        using JsonDocument output = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(output.RootElement, "output");
        Assert.AreEqual("stdout", output.RootElement.GetProperty("body").GetProperty("category").GetString());
        Assert.AreEqual(
            "transport-π-é",
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
    [DataRow(null)]
    [DataRow(false)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedLaunchActivatesCoreClrAndForwardsTargetOutput(bool? stopAtEntry)
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
                noDebug: false,
                stopAtEntry: stopAtEntry),
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
            "transport-π-é",
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
