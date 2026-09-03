using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies source-level stepping through real managed caller and callee frames.
/// </summary>
public sealed partial class DapSessionTests
{
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
            Assert.IsTrue(DebuggerTestPath.AreEquivalent(sourcePath, framePath));
            Assert.AreEqual(breakpointLine, frameLine);

            int modulesSequence = await client.SendRequestAsync(
                "modules",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument modules = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(modules.RootElement, modulesSequence, "modules", success: true);
            JsonElement[] moduleItems = [.. modules.RootElement
                .GetProperty("body")
                .GetProperty("modules")
                .EnumerateArray()];
            Assert.AreEqual(
                moduleItems.Length,
                modules.RootElement.GetProperty("body").GetProperty("totalModules").GetInt32());
            Assert.IsGreaterThan(0, moduleItems.Length);
            string processHost = ResolveTestProcessHost();
            JsonElement fixtureModule = moduleItems.Single(module => DebuggerTestPath.AreEquivalent(
                module.TryGetProperty("path", out JsonElement path)
                    ? path.GetString()
                    : null,
                processHost));
            Assert.IsGreaterThan(0, fixtureModule.GetProperty("id").GetInt32());
            Assert.AreEqual(Path.GetFileName(processHost), fixtureModule.GetProperty("name").GetString());
            Assert.AreEqual("Symbols loaded.", fixtureModule.GetProperty("symbolStatus").GetString());
            Assert.AreEqual(
                Path.ChangeExtension(processHost, ".pdb"),
                fixtureModule.GetProperty("symbolFilePath").GetString());

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
            Assert.IsTrue(DebuggerTestPath.AreEquivalent(sourcePath, framePath));
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
            Assert.IsTrue(DebuggerTestPath.AreEquivalent(sourcePath, framePath));
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
            Assert.IsTrue(DebuggerTestPath.AreEquivalent(sourcePath, framePath));
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
                DebuggerTestPath.AreEquivalent(
                    source.GetProperty("path").GetString(),
                    sourcePath));
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

}
