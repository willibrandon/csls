using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies breakpoint and module state across collectible runtime module churn.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Rebinds a pending source breakpoint on load and retires it on unload.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CollectibleModuleLoadAndUnloadRebindsSourceBreakpoint()
    {
        string repositoryRoot = FindRepositoryRoot();
        string fixtureAssembly = Path.Join(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Debugger.Fixtures.CSharp",
            "debug",
            "Csls.Debugger.Fixtures.CSharp.dll");
        string sourcePath = Path.Join(
            repositoryRoot,
            "test-assets",
            "Csls.Debugger.Fixtures.CSharp",
            "Program.cs");
        int breakpointLine = FindSourceLine(
            await File.ReadAllLinesAsync(
                sourcePath,
                TestContext.CancellationToken).ConfigureAwait(false),
            "answer++;");
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-module-churn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        string loadSignal = Path.Join(testDirectory, "load.signal");
        string fixtureSignal = Path.Join(testDirectory, "fixture.signal");
        string unloadedSignal = Path.Join(testDirectory, "unloaded.signal");
        string finishSignal = Path.Join(testDirectory, "finish.signal");
        try
        {
            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            (int launchSequence, int breakpointId, int configurationSequence) =
                await StartModuleChurnTargetAsync(
                    client,
                    fixtureAssembly,
                    sourcePath,
                    breakpointLine,
                    loadSignal,
                    fixtureSignal,
                    unloadedSignal,
                    finishSignal).ConfigureAwait(false);
            await ReadModuleChurnReadyAsync(
                client,
                launchSequence,
                configurationSequence).ConfigureAwait(false);

            await File.WriteAllTextAsync(
                loadSignal,
                string.Empty,
                TestContext.CancellationToken).ConfigureAwait(false);
            int threadId = await ReadLoadedModuleStopAsync(
                client,
                breakpointId,
                breakpointLine).ConfigureAwait(false);
            await AssertModuleChurnFrameAsync(
                client,
                threadId,
                sourcePath,
                breakpointLine).ConfigureAwait(false);
            await AssertModulePresenceAsync(client, fixtureAssembly, present: true)
                .ConfigureAwait(false);

            await File.WriteAllTextAsync(
                fixtureSignal,
                string.Empty,
                TestContext.CancellationToken).ConfigureAwait(false);
            int continueSequence = await client.SendRequestAsync(
                "continue",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            await ReadContinueAcknowledgementAsync(client, continueSequence)
                .ConfigureAwait(false);
            await WaitForSignalAsync(unloadedSignal).ConfigureAwait(false);
            Assert.AreEqual(
                "unloaded",
                await File.ReadAllTextAsync(
                    unloadedSignal,
                    TestContext.CancellationToken).ConfigureAwait(false));

            int stoppedThreadId = await PauseAfterModuleUnloadAsync(client, breakpointId)
                .ConfigureAwait(false);
            Assert.IsGreaterThan(0, stoppedThreadId);
            await AssertModulePresenceAsync(client, fixtureAssembly, present: false)
                .ConfigureAwait(false);

            await File.WriteAllTextAsync(
                finishSignal,
                string.Empty,
                TestContext.CancellationToken).ConfigureAwait(false);
            int finalContinueSequence = await client.SendRequestAsync(
                "continue",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            await ReadSuccessfulTerminationAsync(
                client,
                finalContinueSequence,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                await client.WaitForExitAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(
                testDirectory,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task<(int LaunchSequence, int BreakpointId, int ConfigurationSequence)>
        StartModuleChurnTargetAsync(
            DapTestClient client,
            string fixtureAssembly,
            string sourcePath,
            int breakpointLine,
            string loadSignal,
            string fixtureSignal,
            string unloadedSignal,
            string finishSignal)
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
                [
                    "--debugger-module-churn-fixture",
                    fixtureAssembly,
                    loadSignal,
                    fixtureSignal,
                    unloadedSignal,
                    finishSignal
                ],
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
        JsonElement breakpoint = breakpoints.RootElement
            .GetProperty("body")
            .GetProperty("breakpoints")[0];
        Assert.IsFalse(breakpoint.GetProperty("verified").GetBoolean());
        int configurationSequence = await client.SendRequestAsync(
            "configurationDone",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        return (
            launchSequence,
            breakpoint.GetProperty("id").GetInt32(),
            configurationSequence);
    }

    private async Task ReadModuleChurnReadyAsync(
        DapTestClient client,
        int launchSequence,
        int configurationSequence)
    {
        bool launchReceived = false;
        bool configurationReceived = false;
        bool processReceived = false;
        bool readyReceived = false;
        while (!launchReceived || !configurationReceived || !processReceived || !readyReceived)
        {
            using JsonDocument message = await client
                .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                int sequence = root.GetProperty("request_seq").GetInt32();
                if (sequence == launchSequence)
                {
                    AssertResponse(root, launchSequence, "launch", success: true);
                    launchReceived = true;
                }
                else if (sequence == configurationSequence)
                {
                    AssertResponse(
                        root,
                        configurationSequence,
                        "configurationDone",
                        success: true);
                    configurationReceived = true;
                }

                continue;
            }

            string? eventName = root.GetProperty("event").GetString();
            processReceived |= eventName == "process";
            readyReceived |= eventName == "output" &&
                root.GetProperty("body").GetProperty("output").GetString() == "ready";
        }
    }

    private async Task<int> ReadLoadedModuleStopAsync(
        DapTestClient client,
        int breakpointId,
        int breakpointLine)
    {
        bool breakpointChanged = false;
        int? threadId = null;
        while (!breakpointChanged || threadId is null)
        {
            using JsonDocument message = await client
                .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement root = message.RootElement;
            string? eventName = root.GetProperty("event").GetString();
            if (eventName == "breakpoint")
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
                threadId = body.GetProperty("threadId").GetInt32();
            }
        }

        return threadId.Value;
    }

    private async Task AssertModuleChurnFrameAsync(
        DapTestClient client,
        int threadId,
        string sourcePath,
        int breakpointLine)
    {
        int sequence = await client.SendRequestAsync(
            "stackTrace",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("threadId", threadId);
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument stack = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(stack.RootElement, sequence, "stackTrace", success: true);
        JsonElement frame = stack.RootElement
            .GetProperty("body")
            .GetProperty("stackFrames")
            .EnumerateArray()
            .Single(candidate => candidate.TryGetProperty("source", out JsonElement source) &&
                DebuggerTestPath.AreEquivalent(source.GetProperty("path").GetString(), sourcePath));
        Assert.AreEqual(breakpointLine, frame.GetProperty("line").GetInt32());
    }

    private async Task AssertModulePresenceAsync(
        DapTestClient client,
        string modulePath,
        bool present)
    {
        int sequence = await client.SendRequestAsync(
            "modules",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument modules = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(modules.RootElement, sequence, "modules", success: true);
        bool found = modules.RootElement
            .GetProperty("body")
            .GetProperty("modules")
            .EnumerateArray()
            .Any(module => DebuggerTestPath.AreEquivalent(
                module.GetProperty("path").GetString(),
                modulePath));
        Assert.AreEqual(present, found);
    }

    private async Task ReadContinueAcknowledgementAsync(
        DapTestClient client,
        int continueSequence)
    {
        bool responseReceived = false;
        bool eventReceived = false;
        while (!responseReceived || !eventReceived)
        {
            using JsonDocument message = await client
                .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                AssertResponse(root, continueSequence, "continue", success: true);
                responseReceived = true;
            }
            else
            {
                eventReceived |= root.GetProperty("event").GetString() == "continued";
            }
        }
    }

    private async Task WaitForSignalAsync(string path)
    {
        while (!File.Exists(path))
        {
            await Task.Delay(25, TestContext.CancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<int> PauseAfterModuleUnloadAsync(
        DapTestClient client,
        int breakpointId)
    {
        int pauseSequence = await client.SendRequestAsync(
            "pause",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        bool breakpointRetired = false;
        bool responseReceived = false;
        int? threadId = null;
        while (!breakpointRetired || !responseReceived || threadId is null)
        {
            using JsonDocument message = await client
                .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                AssertResponse(root, pauseSequence, "pause", success: true);
                responseReceived = true;
                continue;
            }

            string? eventName = root.GetProperty("event").GetString();
            if (eventName == "breakpoint")
            {
                JsonElement breakpoint = root.GetProperty("body").GetProperty("breakpoint");
                if (breakpoint.GetProperty("id").GetInt32() == breakpointId &&
                    !breakpoint.GetProperty("verified").GetBoolean())
                {
                    breakpointRetired = true;
                }
            }
            else if (eventName == "stopped")
            {
                JsonElement body = root.GetProperty("body");
                Assert.AreEqual("pause", body.GetProperty("reason").GetString());
                Assert.IsTrue(responseReceived, "The pause response must precede its stopped event.");
                threadId = body.GetProperty("threadId").GetInt32();
            }
        }

        return threadId.Value;
    }
}
