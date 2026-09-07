using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Shares real DAP operations between test classes with independently owned fixtures.
/// </summary>
public abstract class DapTestContext
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Retains bounded protocol evidence when timeout results replace captured test output.
    /// </summary>
    /// <param name="client">The real DAP client whose exchange belongs to this test.</param>
    /// <returns>The diagnostic scope disposed before disposing the client.</returns>
    private protected DapTestCancellationCapture CaptureProtocolOnCancellation(DapTestClient client)
    {
        string directory = Path.Join(FindRepositoryRoot(), "artifacts", "test-results");
        Directory.CreateDirectory(directory);
        string path = Path.Join(directory, $"dap-cancellation-{Guid.NewGuid():N}.log");
        return new DapTestCancellationCapture(client, TestContext.TestName, path, TestContext.CancellationToken);
    }

    /// <summary>
    /// Launches a real target and verifies configuration, process, and entry-stop ordering.
    /// </summary>
    private protected async Task<(int ThreadId, int ProcessId)> LaunchAtEntryAsync(
        DapTestClient client, string program, string[] arguments, bool initializeClient = true,
        IReadOnlyDictionary<string, string>? sourceFileMap = null, bool? requireExactSource = null,
        Action<Utf8JsonWriter>? writeProperties = null)
    {
        if (initializeClient)
        {
            int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken,
                writeProperties: writer => writer.WriteBoolean("supportsVariablePaging", true)).ConfigureAwait(false);
            using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(response.RootElement, initialize, "initialize", success: true);
        }

        int launch = await client.SendRequestAsync("launch", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("program", program);
            writer.WriteBoolean("stopAtEntry", true);
            writer.WriteStartArray("args");
            foreach (string argument in arguments)
            {
                writer.WriteStringValue(argument);
            }

            writer.WriteEndArray();
            if (sourceFileMap is null)
            {
                WriteDefaultSourceFileMap(writer);
            }
            else
            {
                writer.WriteStartObject("sourceFileMap");
                foreach ((string buildPath, string localPath) in sourceFileMap)
                {
                    writer.WriteString(buildPath, localPath);
                }

                writer.WriteEndObject();
            }

            if (requireExactSource is bool exactSource)
            {
                writer.WriteBoolean("requireExactSource", exactSource);
            }
            writer.WriteStartObject("env");
            writer.WriteString("CSLS_DEBUGGER_ENTRY_VALUE", "entry-result");
            writer.WriteString("--print-environment", "entry-mutated");
            writer.WriteEndObject();
            writeProperties?.Invoke(writer);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(initialized.RootElement, "initialized");
        }

        int configuration = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, configuration, "configurationDone", success: true);
        }

        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, launch, "launch", success: true);
        }

        int processId;
        using (JsonDocument process = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(process.RootElement, "process");
            processId = process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32();
            Assert.IsGreaterThan(0, processId);
        }

        using JsonDocument stopped = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(stopped.RootElement, "stopped");
        JsonElement body = stopped.RootElement.GetProperty("body");
        Assert.AreEqual("entry", body.GetProperty("reason").GetString());
        Assert.IsTrue(body.GetProperty("allThreadsStopped").GetBoolean());
        int threadId = body.GetProperty("threadId").GetInt32();
        Assert.IsGreaterThan(0, threadId);
        return (threadId, processId);
    }

    /// <summary>
    /// Continues an entry-stopped target and verifies its output and terminal events.
    /// </summary>
    private protected async Task ContinueEntryToExitAsync(DapTestClient client, int threadId, string expectedOutput)
    {
        int sequence = await client.SendRequestAsync("continue", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("threadId", threadId);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        var output = new System.Text.StringBuilder();
        bool responseReceived = false;
        bool continuedReceived = false;
        bool exitedReceived = false;
        bool terminatedReceived = false;
        while (!terminatedReceived)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                AssertResponse(root, sequence, "continue", success: true);
                Assert.IsFalse(responseReceived);
                responseReceived = true;
                continue;
            }

            string? eventName = root.GetProperty("event").GetString();
            switch (eventName)
            {
                case "continued":
                    Assert.IsFalse(continuedReceived);
                    continuedReceived = true;
                    break;
                case "output":
                    Assert.AreEqual("stdout", root.GetProperty("body").GetProperty("category").GetString());
                    _ = output.Append(root.GetProperty("body").GetProperty("output").GetString());
                    break;
                case "exited":
                    Assert.IsFalse(exitedReceived);
                    Assert.AreEqual(0, root.GetProperty("body").GetProperty("exitCode").GetInt32());
                    exitedReceived = true;
                    break;
                case "terminated":
                    Assert.IsTrue(exitedReceived);
                    terminatedReceived = true;
                    break;
                default:
                    Assert.Fail($"Unexpected event after entry continuation: {root.GetRawText()}");
                    break;
            }
        }

        Assert.IsTrue(responseReceived);
        Assert.IsTrue(continuedReceived);
        Assert.AreEqual(expectedOutput, output.ToString());
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    /// <summary>
    /// Evaluates an expression over DAP and verifies the requested response outcome.
    /// </summary>
    private protected static async Task<JsonElement> ReadEvaluationAsync(
        DapTestClient client,
        int frameId,
        string expression,
        bool success,
        CancellationToken cancellationToken)
    {
        int sequence = await client.SendRequestAsync(
            "evaluate",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("expression", expression);
                writer.WriteNumber("frameId", frameId);
                writer.WriteString("context", "watch");
                writer.WriteEndObject();
            },
            cancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(cancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "evaluate", success);
        return (success
            ? response.RootElement.GetProperty("body")
            : response.RootElement).Clone();
    }

    /// <summary>
    /// Maps deterministic build paths to the current test repository.
    /// </summary>
    private protected static void WriteDefaultSourceFileMap(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("sourceFileMap");
        writer.WriteString("/_/", FindRepositoryRoot());
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes an empty DAP argument object.
    /// </summary>
    private protected static void WriteEmptyObject(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a source breakpoint request for one authored line.
    /// </summary>
    private protected static void WriteSourceBreakpointArguments(
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

    /// <summary>
    /// Verifies the identity, command, and outcome of a DAP response.
    /// </summary>
    private protected static void AssertResponse(
        JsonElement message,
        int requestSequence,
        string command,
        bool success)
    {
        Assert.AreEqual("response", message.GetProperty("type").GetString(), message.ToString());
        Assert.AreEqual(requestSequence, message.GetProperty("request_seq").GetInt32());
        Assert.AreEqual(command, message.GetProperty("command").GetString());
        Assert.AreEqual(
            success,
            message.GetProperty("success").GetBoolean(),
            message.ToString());
    }

    /// <summary>
    /// Verifies the type and name of a DAP event.
    /// </summary>
    private protected static void AssertEvent(JsonElement message, string eventName)
    {
        Assert.AreEqual("event", message.GetProperty("type").GetString(), message.ToString());
        Assert.AreEqual(eventName, message.GetProperty("event").GetString(), message.ToString());
    }

    /// <summary>
    /// Resolves the built real-process fixture in the current repository.
    /// </summary>
    private protected static string ResolveTestProcessHost()
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

    /// <summary>
    /// Resolves the current test repository from its source path.
    /// </summary>
    private protected static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
        => DebuggerTestEnvironment.FindRepositoryRoot(sourcePath);

    /// <summary>
    /// Sends cancellation for one outstanding DAP request.
    /// </summary>
    private protected Task<int> SendRequestCancellationAsync(DapTestClient client, int sequence) =>
        client.SendRequestAsync("cancel", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("requestId", sequence);
            writer.WriteEndObject();
        }, TestContext.CancellationToken);

    /// <summary>
    /// Cancels a queued request and verifies both protocol responses.
    /// </summary>
    private protected async Task AssertQueuedRequestCanceledAsync(DapTestClient client, int sequence, string command)
    {
        int cancelSequence = await SendRequestCancellationAsync(client, sequence).ConfigureAwait(false);
        using JsonDocument canceled = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(canceled.RootElement, sequence, command, success: false);
        Assert.AreEqual("cancelled", canceled.RootElement.GetProperty("message").GetString());
        using JsonDocument acknowledgement = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(acknowledgement.RootElement, cancelSequence, "cancel", success: true);
    }

    /// <summary>
    /// Finds an authored fixture line containing the required source text.
    /// </summary>
    private protected static int FindSourceLine(IReadOnlyList<string> sourceLines, string text) =>
        sourceLines
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(candidate => candidate.Line.Contains(text, StringComparison.Ordinal))
            .Number;

    /// <summary>
    /// Observes startup responses and verified breakpoint events through the first stop.
    /// </summary>
    private protected static async Task<int> ReadInitialBreakpointStopAsync(
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
                threadId = body.GetProperty("threadId").GetInt32();
                string? reason = body.GetProperty("reason").GetString();
                string evidence = reason == "breakpoint" ? string.Empty :
                    await DapStoppedThreadDiagnostics.CaptureAsync(client, threadId.Value, cancellationToken)
                        .ConfigureAwait(false);
                Assert.AreEqual("breakpoint", reason, $"{root.GetRawText()}{Environment.NewLine}{evidence}");
            }
        }

        return threadId.Value;
    }

    /// <summary>
    /// Reads the first managed frame matching the expected source file.
    /// </summary>
    private protected static async Task<(string Name, string? SourcePath, int Line)> ReadSourceFrameAsync(
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
                source.TryGetProperty("path", out JsonElement path) &&
                DebuggerTestPath.AreEquivalent(
                    path.GetString(),
                    sourcePath));
        return (
            frame.GetProperty("name").GetString()!,
            frame.GetProperty("source").GetProperty("path").GetString(),
            frame.GetProperty("line").GetInt32());
    }

    /// <summary>
    /// Reads and verifies a bounded DAP stack page.
    /// </summary>
    private protected async Task<JsonElement> ReadDeepStackPageAsync(DapTestClient client, int threadId, int start, int levels)
    {
        int sequence = await SendDeepStackRequestAsync(client, threadId, start, levels).ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "stackTrace", success: true);
        return response.RootElement.GetProperty("body").Clone();
    }

    /// <summary>
    /// Requests a bounded stack page for one stopped thread.
    /// </summary>
    private protected Task<int> SendDeepStackRequestAsync(DapTestClient client, int threadId, int start, int levels) =>
        client.SendRequestAsync("stackTrace", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("threadId", threadId);
            writer.WriteNumber("startFrame", start);
            writer.WriteNumber("levels", levels);
            writer.WriteEndObject();
        }, TestContext.CancellationToken);

    /// <summary>
    /// Reads and verifies a requested managed instruction page.
    /// </summary>
    private protected async Task<JsonElement[]> ReadDisassemblyAsync(
        DapTestClient client,
        string reference,
        long offset,
        long instructionOffset,
        int instructionCount)
    {
        int sequence = await client.SendRequestAsync(
            "disassemble",
            writer => WriteDisassemblyArguments(
                writer,
                reference,
                offset,
                instructionOffset,
                instructionCount),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "disassemble", success: true);
        return [.. response.RootElement.GetProperty("body").GetProperty("instructions")
            .EnumerateArray().Select(static instruction => instruction.Clone())];
    }

    /// <summary>
    /// Writes the address and paging arguments for managed disassembly.
    /// </summary>
    private protected static void WriteDisassemblyArguments(
        Utf8JsonWriter writer,
        string reference,
        long offset,
        long instructionOffset,
        int instructionCount)
    {
        writer.WriteStartObject();
        writer.WriteString("memoryReference", reference);
        writer.WriteNumber("offset", offset);
        writer.WriteNumber("instructionOffset", instructionOffset);
        writer.WriteNumber("instructionCount", instructionCount);
        writer.WriteBoolean("resolveSymbols", true);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Verifies the available instruction-pointer destination for a source line.
    /// </summary>
    private protected async Task<int> ReadGotoTargetAsync(
        DapTestClient client,
        string sourcePath,
        int line)
    {
        int sequence = await client.SendRequestAsync(
            "gotoTargets",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartObject("source");
                writer.WriteString("path", sourcePath);
                writer.WriteEndObject();
                writer.WriteNumber("line", line);
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "gotoTargets", success: true);
        JsonElement[] targets = [.. response.RootElement.GetProperty("body")
            .GetProperty("targets").EnumerateArray()];
        Assert.HasCount(1, targets);
        Assert.AreEqual(line, targets[0].GetProperty("line").GetInt32());
        Assert.IsFalse(string.IsNullOrWhiteSpace(targets[0]
            .GetProperty("instructionPointerReference").GetString()));
        return targets[0].GetProperty("id").GetInt32();
    }

    /// <summary>
    /// Disconnects a stopped target and verifies the response.
    /// </summary>
    private protected async Task DisconnectStoppedSessionAsync(DapTestClient client)
    {
        int sequence = await client.SendRequestAsync(
            "disconnect",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        while (true)
        {
            using JsonDocument response = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            JsonElement root = response.RootElement;
            if (root.GetProperty("type").GetString() == "response" &&
                root.GetProperty("request_seq").GetInt32() == sequence)
            {
                AssertResponse(root, sequence, "disconnect", success: true);
                return;
            }
        }
    }

    /// <summary>
    /// Initializes the adapter and prepares a real language fixture launch.
    /// </summary>
    private protected async Task InitializeAndLaunchAsync(
        DapTestClient client,
        string program,
        string waitPath,
        bool suppressJitOptimizations = false)
    {
        int initializeSequence = await client.SendInitializeRequestAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(
            initialize.RootElement,
            initializeSequence,
            "initialize",
            success: true);
        _ = await client.SendRequestAsync(
            "launch",
            writer => WriteLaunchArguments(
                writer,
                program,
                [waitPath, "41", "ready"],
                wait: true,
                noDebug: false,
                suppressJitOptimizations: suppressJitOptimizations),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");
    }

    /// <summary>
    /// Configures a source breakpoint and waits for the fixture to stop there.
    /// </summary>
    private protected async Task<int> ConfigureBreakpointAsync(
        DapTestClient client,
        string sourcePath,
        int breakpointLine,
        string? condition = null)
    {
        _ = await client.SendRequestAsync(
            "setBreakpoints",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartObject("source");
                writer.WriteString("path", sourcePath);
                writer.WriteEndObject();
                writer.WriteStartArray("breakpoints");
                writer.WriteStartObject();
                writer.WriteNumber("line", breakpointLine);
                if (condition is not null)
                {
                    writer.WriteString("condition", condition);
                }

                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument pending = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "configurationDone",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        while (true)
        {
            using JsonDocument message = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.TryGetProperty("event", out JsonElement eventName) &&
                eventName.GetString() == "stopped")
            {
                string? reason = root.GetProperty("body").GetProperty("reason").GetString();
                Assert.AreEqual(
                    "breakpoint",
                    reason,
                    root.GetRawText());
                return root.GetProperty("body").GetProperty("threadId").GetInt32();
            }
        }
    }

    /// <summary>
    /// Disconnects the session and verifies successful adapter shutdown.
    /// </summary>
    private protected async Task DisconnectAsync(DapTestClient client)
    {
        int sequence = await client.SendRequestAsync(
            "disconnect",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        while (true)
        {
            using JsonDocument message = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            if (message.RootElement.TryGetProperty("request_seq", out JsonElement requestSequence) &&
                requestSequence.GetInt32() == sequence)
            {
                AssertResponse(
                    message.RootElement,
                    sequence,
                    "disconnect",
                    success: true);
                break;
            }
        }

        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Writes a real-process launch request with source mappings and target options.
    /// </summary>
    private protected static void WriteLaunchArguments(
        Utf8JsonWriter writer,
        string processHost,
        IReadOnlyList<string> arguments,
        bool wait,
        bool noDebug = true,
        bool suppressJitOptimizations = false,
        bool? stopAtEntry = null,
        bool? showRawValues = null)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("noDebug", noDebug);
        writer.WriteString("program", processHost);
        writer.WriteBoolean("suppressJITOptimizations", suppressJitOptimizations);
        if (stopAtEntry is bool entryStop)
        {
            writer.WriteBoolean("stopAtEntry", entryStop);
        }

        if (showRawValues is bool rawValues)
        {
            writer.WriteStartObject("expressionEvaluationOptions");
            writer.WriteBoolean("showRawValues", rawValues);
            writer.WriteEndObject();
        }

        writer.WriteStartArray("args");
        foreach (string argument in arguments)
        {
            writer.WriteStringValue(argument);
        }

        writer.WriteEndArray();
        WriteDefaultSourceFileMap(writer);
        if (!wait)
        {
            writer.WriteStartObject("env");
            writer.WriteString("CSLS_DEBUGGER_TEST_VALUE", "transport-π-é");
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }
}
