using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies DAP sequencing and target ownership through production sessions.
/// </summary>
[TestClass]
public sealed class DapSessionTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

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
        Assert.IsTrue(capabilities.GetProperty("supportsVariablePaging").GetBoolean());
        Assert.HasCount(2, capabilities.EnumerateObject().ToArray());

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

    /// <summary>
    /// Resolves a pending Portable PDB source breakpoint and stops on its runtime callback.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedSourceBreakpointBindsAndStopsAtRequestedStatement()
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourcePath = Path.Join(
            repositoryRoot,
            "tests",
            "Csls.TestProcessHost",
            "DebuggerFixture.cs");
        string[] sourceLines = await File.ReadAllLinesAsync(
            sourcePath,
            TestContext.CancellationToken).ConfigureAwait(false);
        int breakpointLine = sourceLines
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(static candidate => candidate.Line.Contains(
                "int localNumber = number + 1;",
                StringComparison.Ordinal))
            .Number;
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-breakpoint-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
            _ = await client.SendRequestAsync(
                "initialize",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialize = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            int launchSequence = await client.SendRequestAsync(
                "launch",
                writer => WriteLaunchArguments(
                    writer,
                    ResolveTestProcessHost(),
                    ["--debugger-fixture", waitPath],
                    wait: true,
                    noDebug: false),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialized = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(initialized.RootElement, "initialized");

            int setBreakpointsSequence = await client.SendRequestAsync(
                "setBreakpoints",
                writer => WriteSourceBreakpointArguments(writer, sourcePath, breakpointLine),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument pendingBreakpoints = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                pendingBreakpoints.RootElement,
                setBreakpointsSequence,
                "setBreakpoints",
                success: true);
            JsonElement pendingBreakpoint = pendingBreakpoints.RootElement
                .GetProperty("body")
                .GetProperty("breakpoints")[0];
            Assert.IsFalse(pendingBreakpoint.GetProperty("verified").GetBoolean());
            Assert.AreEqual(breakpointLine, pendingBreakpoint.GetProperty("line").GetInt32());
            int breakpointId = pendingBreakpoint.GetProperty("id").GetInt32();

            int configurationSequence = await client.SendRequestAsync(
                "configurationDone",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            bool configurationReceived = false;
            bool launchReceived = false;
            bool processReceived = false;
            bool breakpointChanged = false;
            int? stoppedThreadId = null;
            while (!configurationReceived || !launchReceived || !processReceived ||
                !breakpointChanged || stoppedThreadId is null)
            {
                using JsonDocument message = await client
                    .ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                JsonElement root = message.RootElement;
                if (root.GetProperty("type").GetString() == "response")
                {
                    int requestSequence = root.GetProperty("request_seq").GetInt32();
                    if (requestSequence == configurationSequence)
                    {
                        AssertResponse(
                            root,
                            configurationSequence,
                            "configurationDone",
                            success: true);
                        configurationReceived = true;
                    }
                    else if (requestSequence == launchSequence)
                    {
                        AssertResponse(root, launchSequence, "launch", success: true);
                        launchReceived = true;
                    }

                    continue;
                }

                string? eventName = root.GetProperty("event").GetString();
                if (eventName == "process")
                {
                    Assert.IsGreaterThan(
                        0,
                        root.GetProperty("body").GetProperty("systemProcessId").GetInt32());
                    processReceived = true;
                }
                else if (eventName == "breakpoint")
                {
                    JsonElement breakpoint = root.GetProperty("body").GetProperty("breakpoint");
                    Assert.AreEqual(breakpointId, breakpoint.GetProperty("id").GetInt32());
                    Assert.IsTrue(breakpoint.GetProperty("verified").GetBoolean());
                    Assert.AreEqual(breakpointLine, breakpoint.GetProperty("line").GetInt32());
                    breakpointChanged = true;
                }
                else if (eventName == "stopped")
                {
                    JsonElement body = root.GetProperty("body");
                    Assert.AreEqual("breakpoint", body.GetProperty("reason").GetString());
                    Assert.IsTrue(body.GetProperty("allThreadsStopped").GetBoolean());
                    stoppedThreadId = body.GetProperty("threadId").GetInt32();
                }
            }

            int stackSequence = await client.SendRequestAsync(
                "stackTrace",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("threadId", stoppedThreadId.Value);
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument stack = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(stack.RootElement, stackSequence, "stackTrace", success: true);
            JsonElement[] frames = [.. stack.RootElement
                .GetProperty("body")
                .GetProperty("stackFrames")
                .EnumerateArray()];
            JsonElement breakpointFrame = frames.First(frame => string.Equals(
                frame.GetProperty("name").GetString(),
                "Csls.TestProcessHost.DebuggerFixture.WaitForSignal",
                StringComparison.Ordinal));
            Assert.AreEqual(sourcePath, breakpointFrame.GetProperty("source").GetProperty("path").GetString());
            Assert.AreEqual(breakpointLine, breakpointFrame.GetProperty("line").GetInt32());

            await File.WriteAllTextAsync(
                waitPath,
                string.Empty,
                TestContext.CancellationToken).ConfigureAwait(false);
            int continueSequence = await client.SendRequestAsync(
                "continue",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            bool continueReceived = false;
            bool continuedReceived = false;
            bool exitedReceived = false;
            bool terminatedReceived = false;
            while (!continueReceived || !continuedReceived || !exitedReceived || !terminatedReceived)
            {
                using JsonDocument message = await client
                    .ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                JsonElement root = message.RootElement;
                if (root.GetProperty("type").GetString() == "response" &&
                    root.GetProperty("request_seq").GetInt32() == continueSequence)
                {
                    AssertResponse(root, continueSequence, "continue", success: true);
                    continueReceived = true;
                    continue;
                }

                string? eventName = root.GetProperty("event").GetString();
                continuedReceived |= eventName == "continued";
                if (eventName == "exited")
                {
                    Assert.AreEqual(0, root.GetProperty("body").GetProperty("exitCode").GetInt32());
                    exitedReceived = true;
                }

                terminatedReceived |= eventName == "terminated";
            }

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

    /// <summary>
    /// Steps over, into, and out through real CoreCLR source positions.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedSourceStepsTraverseCallerAndCallee()
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourcePath = Path.Join(
            repositoryRoot,
            "tests",
            "Csls.TestProcessHost",
            "DebuggerStepFixture.cs");
        string[] sourceLines = await File.ReadAllLinesAsync(
            sourcePath,
            TestContext.CancellationToken).ConfigureAwait(false);
        int breakpointLine = FindSourceLine(sourceLines, "int seed = 40;");
        int callLine = FindSourceLine(sourceLines, "int answer = AddTwo(seed);");
        int calleeEntryLine = FindSourceLine(sourceLines, "private static int AddTwo(int value)") + 1;
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-step-{Guid.NewGuid():N}.signal");
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
            int threadId = await ReadInitialBreakpointStopAsync(
                client,
                configurationSequence,
                launchSequence,
                TestContext.CancellationToken).ConfigureAwait(false);
            (string frameName, string? framePath, int frameLine) = await ReadSourceFrameAsync(
                client,
                threadId,
                sourcePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("Csls.TestProcessHost.DebuggerStepFixture.Run", frameName);
            Assert.AreEqual(sourcePath, framePath);
            Assert.AreEqual(breakpointLine, frameLine);

            threadId = await StepAndReadStopAsync(
                client,
                "next",
                threadId,
                TestContext.CancellationToken).ConfigureAwait(false);
            (frameName, framePath, frameLine) = await ReadSourceFrameAsync(
                client,
                threadId,
                sourcePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("Csls.TestProcessHost.DebuggerStepFixture.Run", frameName);
            Assert.AreEqual(sourcePath, framePath);
            Assert.AreEqual(callLine, frameLine);

            threadId = await StepAndReadStopAsync(
                client,
                "stepIn",
                threadId,
                TestContext.CancellationToken).ConfigureAwait(false);
            (frameName, framePath, frameLine) = await ReadSourceFrameAsync(
                client,
                threadId,
                sourcePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("Csls.TestProcessHost.DebuggerStepFixture.AddTwo", frameName);
            Assert.AreEqual(sourcePath, framePath);
            Assert.AreEqual(calleeEntryLine, frameLine);

            threadId = await StepAndReadStopAsync(
                client,
                "stepOut",
                threadId,
                TestContext.CancellationToken).ConfigureAwait(false);
            (frameName, framePath, frameLine) = await ReadSourceFrameAsync(
                client,
                threadId,
                sourcePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("Csls.TestProcessHost.DebuggerStepFixture.Run", frameName);
            Assert.AreEqual(sourcePath, framePath);
            Assert.AreEqual(callLine, frameLine);

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

    /// <summary>
    /// Pauses a live managed target, enumerates real runtime threads, and resumes execution.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedPauseEnumeratesThreadsAndContinuesTarget()
    {
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-pause-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
            _ = await client.SendRequestAsync(
                "initialize",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialize = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            _ = await client.SendRequestAsync(
                "launch",
                writer => WriteLaunchArguments(
                    writer,
                    ResolveTestProcessHost(),
                    ["--debugger-fixture", waitPath],
                    wait: true,
                    noDebug: false),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialized = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            _ = await client.SendRequestAsync(
                "configurationDone",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument configuration = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument launch = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument process = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument ready = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(ready.RootElement, "output");
            Assert.AreEqual(
                "ready",
                ready.RootElement.GetProperty("body").GetProperty("output").GetString());

            int pauseSequence = await client.SendRequestAsync(
                "pause",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument stopped = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(stopped.RootElement, "stopped");
            Assert.AreEqual(
                "pause",
                stopped.RootElement.GetProperty("body").GetProperty("reason").GetString());
            Assert.IsTrue(
                stopped.RootElement.GetProperty("body").GetProperty("allThreadsStopped").GetBoolean());
            using JsonDocument pause = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(pause.RootElement, pauseSequence, "pause", success: true);

            int threadsSequence = await client.SendRequestAsync(
                "threads",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument threads = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(threads.RootElement, threadsSequence, "threads", success: true);
            JsonElement[] threadItems = [.. threads.RootElement
                .GetProperty("body")
                .GetProperty("threads")
                .EnumerateArray()];
            Assert.IsNotEmpty(threadItems);
            Assert.IsTrue(threadItems.All(thread => thread.GetProperty("id").GetInt32() > 0));
            Assert.HasCount(
                threadItems.Length,
                threadItems.Select(thread => thread.GetProperty("id").GetInt32()).Distinct().ToArray());

            int fixtureFrameId = 0;
            foreach (JsonElement thread in threadItems)
            {
                int threadId = thread.GetProperty("id").GetInt32();
                int stackSequence = await client.SendRequestAsync(
                    "stackTrace",
                    writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("threadId", threadId);
                        writer.WriteNumber("startFrame", 0);
                        writer.WriteNumber("levels", 64);
                        writer.WriteEndObject();
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                using JsonDocument stack = await client
                    .ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                AssertResponse(stack.RootElement, stackSequence, "stackTrace", success: true);
                JsonElement[] frames = [.. stack.RootElement
                    .GetProperty("body")
                    .GetProperty("stackFrames")
                    .EnumerateArray()];
                Assert.IsGreaterThanOrEqualTo(
                    frames.Length,
                    stack.RootElement.GetProperty("body").GetProperty("totalFrames").GetInt32());
                JsonElement fixtureFrame = frames.FirstOrDefault(frame =>
                    frame.TryGetProperty("source", out JsonElement source) &&
                    source.GetProperty("path").GetString() is string path &&
                    path.EndsWith(
                        Path.Join("tests", "Csls.TestProcessHost", "DebuggerFixture.cs"),
                        StringComparison.Ordinal) &&
                    frame.GetProperty("line").GetInt32() > 0);
                if (fixtureFrame.ValueKind != JsonValueKind.Undefined)
                {
                    fixtureFrameId = fixtureFrame.GetProperty("id").GetInt32();
                    break;
                }
            }

            Assert.IsGreaterThan(0, fixtureFrameId, "No managed stack frame resolved to the fixture source.");

            int scopesSequence = await client.SendRequestAsync(
                "scopes",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("frameId", fixtureFrameId);
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument scopes = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(scopes.RootElement, scopesSequence, "scopes", success: true);
            JsonElement[] scopeItems = [.. scopes.RootElement
                .GetProperty("body")
                .GetProperty("scopes")
                .EnumerateArray()];
            Assert.HasCount(2, scopeItems);
            int staleVariablesReference = scopeItems[0]
                .GetProperty("variablesReference")
                .GetInt32();

            Dictionary<string, JsonElement[]> variablesByScope = new(StringComparer.Ordinal);
            foreach (JsonElement scope in scopeItems)
            {
                int reference = scope.GetProperty("variablesReference").GetInt32();
                int variablesSequence = await client.SendRequestAsync(
                    "variables",
                    writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("variablesReference", reference);
                        writer.WriteEndObject();
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                using JsonDocument variables = await client
                    .ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                AssertResponse(variables.RootElement, variablesSequence, "variables", success: true);
                variablesByScope.Add(
                    scope.GetProperty("name").GetString()!,
                    [.. variables.RootElement
                        .GetProperty("body")
                        .GetProperty("variables")
                        .EnumerateArray()
                        .Select(variable => variable.Clone())]);
            }

            JsonElement[] arguments = variablesByScope["Arguments"];
            Dictionary<string, JsonElement> argumentsByName = arguments.ToDictionary(
                variable => variable.GetProperty("name").GetString()!,
                StringComparer.Ordinal);
            Assert.AreEqual("42", argumentsByName["number"].GetProperty("value").GetString());
            Assert.AreEqual("int", argumentsByName["number"].GetProperty("type").GetString());
            Assert.AreEqual(
                "\"answer\"",
                argumentsByName["text"].GetProperty("value").GetString());
            Assert.AreEqual("string", argumentsByName["text"].GetProperty("type").GetString());
            JsonElement[] locals = variablesByScope["Locals"];
            Dictionary<string, JsonElement> localsByName = locals.ToDictionary(
                variable => variable.GetProperty("name").GetString()!,
                StringComparer.Ordinal);
            Assert.AreEqual("43", localsByName["localNumber"].GetProperty("value").GetString());
            Assert.AreEqual(
                "\"answer!\"",
                localsByName["localText"].GetProperty("value").GetString());

            int continueSequence = await client.SendRequestAsync(
                "continue",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument continued = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(continued.RootElement, "continued");
            using JsonDocument continueResponse = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                continueResponse.RootElement,
                continueSequence,
                "continue",
                success: true);

            int secondPauseSequence = await client.SendRequestAsync(
                "pause",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument secondStopped = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(secondStopped.RootElement, "stopped");
            using JsonDocument secondPause = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(secondPause.RootElement, secondPauseSequence, "pause", success: true);

            int staleVariablesSequence = await client.SendRequestAsync(
                "variables",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("variablesReference", staleVariablesReference);
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument staleVariables = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                staleVariables.RootElement,
                staleVariablesSequence,
                "variables",
                success: false);
            Assert.Contains(
                "stale",
                staleVariables.RootElement.GetProperty("message").GetString()!,
                StringComparison.OrdinalIgnoreCase);

            int secondContinueSequence = await client.SendRequestAsync(
                "continue",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument secondContinued = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(secondContinued.RootElement, "continued");
            using JsonDocument secondContinue = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                secondContinue.RootElement,
                secondContinueSequence,
                "continue",
                success: true);

            await File.WriteAllTextAsync(
                waitPath,
                string.Empty,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument exited = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(exited.RootElement, "exited");
            using JsonDocument terminated = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(terminated.RootElement, "terminated");
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

    /// <summary>
    /// Rejects invalid request ordering without preventing later initialization.
    /// </summary>
    [TestMethod]
    public async Task InvalidStateRequestDoesNotCorruptSession()
    {
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        int threadsSequence = await client.SendRequestAsync(
            "threads",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument threads = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(threads.RootElement, threadsSequence, "threads", success: false);
        Assert.Contains(
            "Created",
            threads.RootElement.GetProperty("message").GetString()!,
            StringComparison.Ordinal);

        int initializeSequence = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);

        int repeatedSequence = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument repeated = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(repeated.RootElement, repeatedSequence, "initialize", success: false);
        Assert.Contains(
            "Initialized",
            repeated.RootElement.GetProperty("message").GetString()!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects an unadvertised graceful-terminate request without ending the connection.
    /// </summary>
    [TestMethod]
    public async Task UnadvertisedTerminateRequestReturnsUnsupportedFailure()
    {
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        int terminateSequence = await client.SendRequestAsync(
            "terminate",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument terminate = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);

        AssertResponse(terminate.RootElement, terminateSequence, "terminate", success: false);
        Assert.Contains(
            "not supported",
            terminate.RootElement.GetProperty("message").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Terminates a real long-running target when its DAP owner disconnects.
    /// </summary>
    [TestMethod]
    public async Task DisconnectTerminatesRunningOwnedProcessTree()
    {
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "launch",
            writer => WriteLaunchArguments(
                writer,
                ResolveTestProcessHost(),
                ["--wait-for-standard-input"],
                wait: true),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "configurationDone",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument configuration = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        using JsonDocument launch = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        using JsonDocument process = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        int processId = process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32();

        int disconnectSequence = await client.SendRequestAsync(
            "disconnect",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        JsonDocument message;
        do
        {
            message = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            if (message.RootElement.TryGetProperty("request_seq", out JsonElement requestSequence) &&
                requestSequence.GetInt32() == disconnectSequence)
            {
                break;
            }

            message.Dispose();
        }
        while (true);
        using (message)
        {
            AssertResponse(message.RootElement, disconnectSequence, "disconnect", success: true);
        }

        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        await AssertProcessExitedAsync(processId, TestContext.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Terminates an owned target when the adapter connection is canceled without disconnect.
    /// </summary>
    [TestMethod]
    public async Task ConnectionCancellationTerminatesRunningOwnedProcessTree()
    {
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "launch",
            writer => WriteLaunchArguments(
                writer,
                ResolveTestProcessHost(),
                ["--wait-for-standard-input"],
                wait: true),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "configurationDone",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument configuration = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        using JsonDocument launch = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        using JsonDocument process = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        int processId = process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32();

        await client.DisposeAsync().ConfigureAwait(false);

        await AssertProcessExitedAsync(processId, TestContext.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Terminates an owned target when the client closes its protocol stream abruptly.
    /// </summary>
    [TestMethod]
    public async Task EndOfStreamTerminatesRunningOwnedProcessTree()
    {
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "launch",
            writer => WriteLaunchArguments(
                writer,
                ResolveTestProcessHost(),
                ["--wait-for-standard-input"],
                wait: true),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "configurationDone",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument configuration = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        using JsonDocument launch = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        using JsonDocument process = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        int processId = process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32();

        await client.CloseProtocolAsync().ConfigureAwait(false);

        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        await AssertProcessExitedAsync(processId, TestContext.CancellationToken).ConfigureAwait(false);
    }

    private static void WriteLaunchArguments(
        Utf8JsonWriter writer,
        string processHost,
        IReadOnlyList<string> arguments,
        bool wait,
        bool noDebug = true)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("noDebug", noDebug);
        writer.WriteString("program", processHost);
        writer.WriteStartArray("args");
        foreach (string argument in arguments)
        {
            writer.WriteStringValue(argument);
        }

        writer.WriteEndArray();
        if (!wait)
        {
            writer.WriteStartObject("env");
            writer.WriteString("CSLS_DEBUGGER_TEST_VALUE", "transport-value");
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static int FindSourceLine(IReadOnlyList<string> sourceLines, string text) =>
        sourceLines
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(candidate => candidate.Line.Contains(text, StringComparison.Ordinal))
            .Number;

    private static async Task<int> ReadInitialBreakpointStopAsync(
        DapTestClient client,
        int configurationSequence,
        int launchSequence,
        CancellationToken cancellationToken)
    {
        bool configurationReceived = false;
        bool launchReceived = false;
        bool processReceived = false;
        bool breakpointChanged = false;
        int? threadId = null;
        while (!configurationReceived || !launchReceived || !processReceived ||
            !breakpointChanged || threadId is null)
        {
            using JsonDocument message = await client
                .ReadMessageAsync(cancellationToken)
                .ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                int requestSequence = root.GetProperty("request_seq").GetInt32();
                if (requestSequence == configurationSequence)
                {
                    AssertResponse(
                        root,
                        configurationSequence,
                        "configurationDone",
                        success: true);
                    configurationReceived = true;
                }
                else if (requestSequence == launchSequence)
                {
                    AssertResponse(root, launchSequence, "launch", success: true);
                    launchReceived = true;
                }

                continue;
            }

            string? eventName = root.GetProperty("event").GetString();
            if (eventName == "process")
            {
                processReceived = true;
            }
            else if (eventName == "breakpoint")
            {
                Assert.IsTrue(
                    root.GetProperty("body")
                        .GetProperty("breakpoint")
                        .GetProperty("verified")
                        .GetBoolean());
                breakpointChanged = true;
            }
            else if (eventName == "stopped")
            {
                JsonElement body = root.GetProperty("body");
                Assert.AreEqual("breakpoint", body.GetProperty("reason").GetString());
                threadId = body.GetProperty("threadId").GetInt32();
            }
        }

        return threadId.Value;
    }

    private static async Task<int> StepAndReadStopAsync(
        DapTestClient client,
        string command,
        int threadId,
        CancellationToken cancellationToken)
    {
        int requestSequence = await client.SendRequestAsync(
            command,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("threadId", threadId);
                writer.WriteEndObject();
            },
            cancellationToken).ConfigureAwait(false);
        bool responseReceived = false;
        bool continuedReceived = false;
        int? stoppedThreadId = null;
        while (!responseReceived || !continuedReceived || stoppedThreadId is null)
        {
            using JsonDocument message = await client
                .ReadMessageAsync(cancellationToken)
                .ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                AssertResponse(root, requestSequence, command, success: true);
                responseReceived = true;
                continue;
            }

            string? eventName = root.GetProperty("event").GetString();
            if (eventName == "continued")
            {
                Assert.IsTrue(
                    root.GetProperty("body").GetProperty("allThreadsContinued").GetBoolean());
                continuedReceived = true;
            }
            else if (eventName == "stopped")
            {
                JsonElement body = root.GetProperty("body");
                Assert.AreEqual("step", body.GetProperty("reason").GetString());
                Assert.IsTrue(body.GetProperty("allThreadsStopped").GetBoolean());
                stoppedThreadId = body.GetProperty("threadId").GetInt32();
            }
        }

        return stoppedThreadId.Value;
    }

    private static async Task<(string Name, string? SourcePath, int Line)> ReadSourceFrameAsync(
        DapTestClient client,
        int threadId,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        int requestSequence = await client.SendRequestAsync(
            "stackTrace",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("threadId", threadId);
                writer.WriteEndObject();
            },
            cancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(cancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, requestSequence, "stackTrace", success: true);
        JsonElement frame = response.RootElement
            .GetProperty("body")
            .GetProperty("stackFrames")
            .EnumerateArray()
            .First(candidate =>
                candidate.TryGetProperty("source", out JsonElement source) &&
                string.Equals(
                    source.GetProperty("path").GetString(),
                    sourcePath,
                    StringComparison.Ordinal));
        return (
            frame.GetProperty("name").GetString()!,
            frame.GetProperty("source").GetProperty("path").GetString(),
            frame.GetProperty("line").GetInt32());
    }

    private static async Task ReadSuccessfulTerminationAsync(
        DapTestClient client,
        int continueSequence,
        CancellationToken cancellationToken)
    {
        bool responseReceived = false;
        bool continuedReceived = false;
        bool exitedReceived = false;
        bool terminatedReceived = false;
        while (!responseReceived || !continuedReceived || !exitedReceived || !terminatedReceived)
        {
            using JsonDocument message = await client
                .ReadMessageAsync(cancellationToken)
                .ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                AssertResponse(root, continueSequence, "continue", success: true);
                responseReceived = true;
                continue;
            }

            string? eventName = root.GetProperty("event").GetString();
            continuedReceived |= eventName == "continued";
            if (eventName == "exited")
            {
                Assert.AreEqual(0, root.GetProperty("body").GetProperty("exitCode").GetInt32());
                exitedReceived = true;
            }

            terminatedReceived |= eventName == "terminated";
        }
    }

    private static void WriteEmptyObject(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteEndObject();
    }

    private static void WriteSourceBreakpointArguments(
        Utf8JsonWriter writer,
        string sourcePath,
        int line)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("source");
        writer.WriteString("name", Path.GetFileName(sourcePath));
        writer.WriteString("path", sourcePath);
        writer.WriteEndObject();
        writer.WriteStartArray("breakpoints");
        writer.WriteStartObject();
        writer.WriteNumber("line", line);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void AssertResponse(
        JsonElement message,
        int requestSequence,
        string command,
        bool success)
    {
        Assert.AreEqual("response", message.GetProperty("type").GetString());
        Assert.AreEqual(requestSequence, message.GetProperty("request_seq").GetInt32());
        Assert.AreEqual(command, message.GetProperty("command").GetString());
        Assert.AreEqual(
            success,
            message.GetProperty("success").GetBoolean(),
            message.ToString());
    }

    private static void AssertEvent(JsonElement message, string eventName)
    {
        Assert.AreEqual("event", message.GetProperty("type").GetString());
        Assert.AreEqual(eventName, message.GetProperty("event").GetString());
    }

    private static string ResolveTestProcessHost()
    {
        string repositoryRoot = FindRepositoryRoot();
        return Path.Join(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.TestProcessHost",
            "debug",
            "csls-test-process-host.dll");
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        DirectoryInfo? directory = new FileInfo(sourcePath).Directory;
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the csls repository root.");
    }

    private static async Task AssertProcessExitedAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        }

        Assert.Fail($"Debugger-owned process {processId} remained alive after disconnect.");
    }
}
