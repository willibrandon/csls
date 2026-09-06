using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies offline dump inspection and process ownership through the production DAP transport.
/// </summary>
[TestClass]
public sealed class DapDumpTests : DapTestContext
{
    private static readonly string[] s_dumpCapabilities =
        ["supportsModulesRequest", "supportsConfigurationDoneRequest", "supportsCancelRequest"];
    private static readonly string[] s_invalidAttachArguments = ["null", "[]", "{}"];

    /// <summary>
    /// Inspects captured values through the isolated worker while preserving paging, ownership, and read-only state.
    /// </summary>
    /// <param name="includeHeap">Whether the dump retains the heap string referenced by the frame.</param>
    /// <param name="supportsPaging">Whether the editor advertises variable paging.</param>
    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task DumpScopesAndVariablesPreserveCapturedValues(bool includeHeap, bool supportsPaging)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, captureFrameValues: true, includeHeap).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        DapTestClient client = await CreateClientAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        _ = await RequestAsync(client, "initialize", writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("supportsVariablePaging", supportsPaging);
            writer.WriteEndObject();
        }).ConfigureAwait(false);
        int threadId = await OpenDumpAsync(client, fixture.DumpPath, initialize: false).ConfigureAwait(false);
        JsonElement stack = await ReadStackAsync(client, threadId, start: 0, levels: 100).ConfigureAwait(false);
        JsonElement frame = Assert.ContainsSingle(stack.GetProperty("stackFrames").EnumerateArray()
            .Where(item => item.GetProperty("name").GetString()?.Contains(
                "DebuggerFixture.WaitForSignal", StringComparison.Ordinal) == true));
        int frameId = frame.GetProperty("id").GetInt32();
        JsonElement scopes = await RequestAsync(client, "scopes", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("frameId", frameId);
            writer.WriteEndObject();
        }).ConfigureAwait(false);
        JsonElement scopeArray = scopes.GetProperty("scopes");
        Assert.AreEqual(2, scopeArray.GetArrayLength());
        Assert.AreEqual("Arguments", scopeArray[0].GetProperty("name").GetString());
        Assert.AreEqual("Locals", scopeArray[1].GetProperty("name").GetString());
        int arguments = scopeArray[0].GetProperty("variablesReference").GetInt32();
        int locals = scopeArray[1].GetProperty("variablesReference").GetInt32();
        Assert.AreNotEqual(arguments, locals);
        JsonElement values = await ReadDumpVariablesAsync(client, arguments, 0, 0).ConfigureAwait(false);
        Assert.AreEqual(8, values.GetArrayLength());
        Assert.AreEqual("number", values[2].GetProperty("name").GetString());
        Assert.AreEqual("42", values[2].GetProperty("value").GetString());
        Assert.AreEqual("int", values[2].GetProperty("type").GetString());
        if (includeHeap)
        {
            Assert.AreEqual("\"answer\"", values[3].GetProperty("value").GetString());
        }
        else
        {
            string? unavailable = values[3].GetProperty("value").GetString();
            Assert.IsNotNull(unavailable);
            Assert.Contains("Captured value unavailable", unavailable);
            Assert.Contains("readOnly", values[3].GetProperty("presentationHint").GetProperty("attributes")
                .EnumerateArray().Select(attribute => attribute.GetString()));
        }

        JsonElement localValues = await ReadDumpVariablesAsync(client, locals, 0, 2).ConfigureAwait(false);
        Assert.AreEqual("localNumber", localValues[0].GetProperty("name").GetString());
        Assert.AreEqual("43", localValues[0].GetProperty("value").GetString());
        Assert.AreEqual("44", localValues[1].GetProperty("value").GetString());
        Assert.AreEqual("long", localValues[1].GetProperty("type").GetString());
        if (includeHeap)
        {
            JsonElement arraySlot = await ReadDumpVariablesAsync(client, locals, 4, 1).ConfigureAwait(false);
            JsonElement array = supportsPaging ? arraySlot[0] : Assert.ContainsSingle(arraySlot.EnumerateArray()
                .Where(value => value.GetProperty("name").GetString() == "localArray"));
            Assert.AreEqual("localArray", array.GetProperty("name").GetString());
            int arrayReference = array.GetProperty("variablesReference").GetInt32();
            Assert.IsGreaterThan(0, arrayReference);
            if (supportsPaging)
            {
                Assert.AreEqual(3, array.GetProperty("indexedVariables").GetInt32());
            }
            JsonElement children = await ReadDumpVariablesAsync(client, arrayReference, 1, 1, filter: "indexed").ConfigureAwait(false);
            Assert.AreEqual(supportsPaging ? 1 : 3, children.GetArrayLength());
            JsonElement second = children[supportsPaging ? 0 : 1];
            Assert.AreEqual("[1]", second.GetProperty("name").GetString());
            Assert.AreEqual("42", second.GetProperty("value").GetString());
            Assert.AreEqual("int", second.GetProperty("type").GetString());
            Assert.AreEqual(0, second.GetProperty("variablesReference").GetInt32());
            Assert.AreEqual(0, (await ReadDumpVariablesAsync(client, arrayReference, 0, 1, filter: "named")
                .ConfigureAwait(false)).GetArrayLength());
            Assert.AreEqual(children.GetRawText(), (await ReadDumpVariablesAsync(client, arrayReference, 1, 1, filter: "indexed")
                .ConfigureAwait(false)).GetRawText());
        }
        JsonElement next = await ReadDumpVariablesAsync(client, locals, 1, 1).ConfigureAwait(false);
        if (supportsPaging)
        {
            Assert.AreEqual(2, localValues.GetArrayLength());
            Assert.AreEqual(1, next.GetArrayLength());
            Assert.AreEqual(localValues[1].GetRawText(), next[0].GetRawText());
            Assert.AreEqual(0, (await ReadDumpVariablesAsync(client, locals, int.MaxValue, 1)
                .ConfigureAwait(false)).GetArrayLength());
        }
        else
        {
            Assert.IsGreaterThan(2, localValues.GetArrayLength());
            Assert.AreEqual(localValues.GetRawText(), next.GetRawText());
        }

        Assert.AreEqual(0, (await ReadDumpVariablesAsync(client, locals, 0, 1, filter: "indexed")
            .ConfigureAwait(false)).GetArrayLength());
        _ = await ReadDumpVariablesAsync(client, int.MaxValue, 0, 1, success: false).ConfigureAwait(false);
        _ = await ReadDumpVariablesAsync(client, locals, -1, 1, success: false).ConfigureAwait(false);
        _ = await ReadDumpVariablesAsync(client, locals, 0, 1, filter: "invalid", success: false).ConfigureAwait(false);
        _ = await RequestAsync(client, "setVariable", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("variablesReference", locals);
            writer.WriteString("name", "localNumber");
            writer.WriteString("value", "99");
            writer.WriteEndObject();
        }, success: false).ConfigureAwait(false);
        JsonElement recovered = await ReadDumpVariablesAsync(client, locals, 0, 2).ConfigureAwait(false);
        Assert.AreEqual(localValues.GetRawText(), recovered.GetRawText());
        Assert.IsNull(client.TargetProcessId);
        await CloseDumpAsync(client).ConfigureAwait(false);
        using FileStream released = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0L, released.Length);
    }

    /// <summary>
    /// Restores live capabilities and runtime inspection after dump setup fails or is cancelled.
    /// </summary>
    /// <param name="cancel">Whether to cancel configuration instead of failing dump activation.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task LiveLaunchAfterAbandonedDumpRestoresCapabilities(bool cancel)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        DapTestClient client = await CreateClientAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        JsonElement originalCapabilities = await RequestAsync(client, "initialize", WriteEmptyObject)
            .ConfigureAwait(false);
        int attach = await PrepareDumpAsync(client,
            cancel ? fixture.DumpPath : Path.ChangeExtension(fixture.DumpPath, "missing")).ConfigureAwait(false);
        if (cancel)
        {
            await AssertQueuedRequestCanceledAsync(client, attach, "attach").ConfigureAwait(false);
        }
        else
        {
            JsonElement failure = await RequestAsync(client, "configurationDone", WriteEmptyObject,
                success: false).ConfigureAwait(false);
            string? message = failure.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("does not exist", message);
            using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(response.RootElement, attach, "attach", success: false);
            Assert.AreEqual(message,
                response.RootElement.GetProperty("message").GetString());
        }

        Assert.IsNull(client.TargetProcessId);
        int launch = await client.SendRequestAsync("launch", writer => WriteLaunchArguments(writer,
            ResolveTestProcessHost(), ["--print-environment", "CSLS_DEBUGGER_TEST_VALUE"],
            wait: false, noDebug: false, stopAtEntry: true), TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument capabilities = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(capabilities.RootElement, "capabilities");
            Assert.IsTrue(JsonElement.DeepEquals(originalCapabilities,
                capabilities.RootElement.GetProperty("body").GetProperty("capabilities")));
        }
        using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(initialized.RootElement, "initialized");
        }
        _ = await RequestAsync(client, "configurationDone", WriteEmptyObject).ConfigureAwait(false);
        using (JsonDocument launched = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertResponse(launched.RootElement, launch, "launch", success: true);
        }
        using (JsonDocument process = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(process.RootElement, "process");
            Assert.IsGreaterThan(0, process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32());
        }
        using JsonDocument stopped = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(stopped.RootElement, "stopped");
        Assert.AreEqual("entry", stopped.RootElement.GetProperty("body").GetProperty("reason").GetString());
        int threadId = stopped.RootElement.GetProperty("body").GetProperty("threadId").GetInt32();
        JsonElement stack = await ReadStackAsync(client, threadId, start: 0, levels: 1).ConfigureAwait(false);
        JsonElement frame = stack.GetProperty("stackFrames")[0];
        Assert.EndsWith("Program.cs", frame.GetProperty("source").GetProperty("path").GetString());
        JsonElement scopes = await RequestAsync(client, "scopes", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("frameId", frame.GetProperty("id").GetInt32());
            writer.WriteEndObject();
        }).ConfigureAwait(false);
        Assert.Contains("Locals", scopes.GetProperty("scopes").EnumerateArray()
            .Select(scope => scope.GetProperty("name").GetString()));
        await ContinueEntryToExitAsync(client, threadId, "transport-π-é").ConfigureAwait(false);
        using FileStream released = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0L, released.Length);
    }

    /// <summary>
    /// Observes the actual dump worker exit after disconnect or deliberate worker failure.
    /// </summary>
    /// <param name="killWorker">Whether to kill only the observed test-owned dump worker.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(OperatingSystems.Linux)]
    [SupportedOSPlatform("linux")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task DumpWorkerLifetimeMatchesAdapterSession(bool killWorker)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        DapTestClient client = await CreateClientAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        _ = await OpenDumpAsync(client, fixture.DumpPath).ConfigureAwait(false);
        using System.Diagnostics.Process worker = LinuxDebuggerProcessTree.OpenWorker(client.HostProcessId, DumpWorkerPath);
        Assert.AreNotEqual(fixture.ProcessId, worker.Id);
        if (killWorker)
        {
            worker.Kill();
            using JsonDocument terminated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertEvent(terminated.RootElement, "terminated");
            Assert.AreEqual(1, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.Contains("managed dump worker exited", client.Diagnostics.ToString());
        }
        else
        {
            await CloseDumpAsync(client).ConfigureAwait(false);
        }

        await worker.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsTrue(worker.HasExited);
        Assert.IsNull(client.TargetProcessId);
        _ = await Assert.ThrowsExactlyAsync<EndOfStreamException>(async () =>
        {
            using JsonDocument unexpected = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes a configured dump before activation and settles its pending attach request.
    /// </summary>
    /// <param name="terminateDebuggee">The requested live-target ownership flag.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task DisconnectPendingDumpAttachmentSettlesRequests(bool terminateDebuggee)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        DapTestClient client = await CreateClientAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        _ = await RequestAsync(client, "initialize", WriteEmptyObject).ConfigureAwait(false);
        int attach = await PrepareDumpAsync(client, fixture.DumpPath).ConfigureAwait(false);
        int disconnect = await client.SendRequestAsync("disconnect", writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("terminateDebuggee", terminateDebuggee);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument canceled = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(canceled.RootElement, attach, "attach", success: false);
            Assert.AreEqual("cancelled", canceled.RootElement.GetProperty("message").GetString());
        }
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, disconnect, "disconnect", success: true);
        }
        using JsonDocument terminated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertEvent(terminated.RootElement, "terminated");
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        using FileStream released = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0L, released.Length);
    }

    /// <summary>
    /// Reports activation failures to both requests and accepts a valid dump on the same connection.
    /// </summary>
    /// <param name="failure">The real file or runtime-selection failure to exercise.</param>
    [TestMethod]
    [DataRow("missing-dump")]
    [DataRow("corrupt-dump")]
    [DataRow("runtime-index")]
    [DataRow("missing-dac")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task FailedDumpActivationPreservesSession(string failure)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        string invalidPath = Path.ChangeExtension(fixture.DumpPath, "invalid");
        if (failure == "corrupt-dump")
        {
            await File.WriteAllTextAsync(invalidPath, "invalid process dump", TestContext.CancellationToken)
                .ConfigureAwait(false);
        }

        DapTestClient client = await CreateClientAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        _ = await RequestAsync(client, "initialize", WriteEmptyObject).ConfigureAwait(false);
        int attach = await PrepareDumpAsync(client,
            failure is "missing-dump" or "corrupt-dump" ? invalidPath : fixture.DumpPath,
            runtimeIndex: failure == "runtime-index" ? 1 : 0,
            dacPath: failure == "missing-dac" ? invalidPath : null).ConfigureAwait(false);
        JsonElement configurationFailure = await RequestAsync(client, "configurationDone", WriteEmptyObject,
            success: false).ConfigureAwait(false);
        string? message = configurationFailure.GetProperty("message").GetString();
        Assert.IsNotNull(message);
        Assert.IsFalse(string.IsNullOrWhiteSpace(message));
        if (failure is "missing-dump" or "missing-dac")
        {
            Assert.Contains("does not exist", message);
        }
        else if (failure == "runtime-index")
        {
            Assert.Contains("1 managed runtime", message);
        }

        using (JsonDocument failed = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(failed.RootElement, attach, "attach", success: false);
            Assert.AreEqual(message, failed.RootElement.GetProperty("message").GetString());
        }
        using (FileStream released = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.IsGreaterThan(0L, released.Length);
        }

        int threadId = await OpenDumpAsync(client, fixture.DumpPath, initialize: false,
            capabilitiesChanged: false).ConfigureAwait(false);
        JsonElement stack = await ReadStackAsync(client, threadId, start: 0, levels: 1).ConfigureAwait(false);
        Assert.AreEqual(1, stack.GetProperty("stackFrames").GetArrayLength());
        await CloseDumpAsync(client).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels a pending dump attachment before activation and accepts another dump on the same connection.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CancelPendingDumpAttachmentPreservesSession()
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        DapTestClient client = await CreateClientAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        _ = await RequestAsync(client, "initialize", WriteEmptyObject).ConfigureAwait(false);
        int attach = await PrepareDumpAsync(client, fixture.DumpPath).ConfigureAwait(false);
        int cancel = await client.SendRequestAsync("cancel", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("requestId", attach);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument canceled = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(canceled.RootElement, attach, "attach", success: false);
            Assert.AreEqual("cancelled", canceled.RootElement.GetProperty("message").GetString());
        }
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, cancel, "cancel", success: true);
        }

        _ = await RequestAsync(client, "configurationDone", WriteEmptyObject, success: false).ConfigureAwait(false);
        int threadId = await OpenDumpAsync(client, fixture.DumpPath, initialize: false,
            capabilitiesChanged: false).ConfigureAwait(false);
        JsonElement stack = await ReadStackAsync(client, threadId, start: 0, levels: 1).ConfigureAwait(false);
        Assert.AreEqual(1, stack.GetProperty("stackFrames").GetArrayLength());
        await CloseDumpAsync(client).ConfigureAwait(false);
    }

    /// <summary>
    /// Rejects malformed dump selectors over DAP while preserving the initialized session for a valid attachment.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task DumpAttachValidationPreservesInitializedSession()
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        DapTestClient client = await CreateClientAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        _ = await RequestAsync(client, "initialize", WriteEmptyObject).ConfigureAwait(false);
        foreach (JsonDocument invalid in s_invalidAttachArguments.Select(arguments => JsonDocument.Parse(arguments)))
        {
            using (invalid)
            {
                JsonElement failure = await RequestAsync(client, "attach", invalid.RootElement.WriteTo,
                    success: false).ConfigureAwait(false);
                Assert.IsFalse(string.IsNullOrWhiteSpace(failure.GetProperty("message").GetString()));
            }
        }

        (string Name, string Value)[] invalidProperties =
        [
            ("dumpPath", "null"), ("dumpPath", "false"), ("dumpPath", "\"\""),
            ("dumpPath", "\"relative.dmp\""), ("runtimeIndex", "-1"),
            ("runtimeIndex", "0.5"), ("runtimeIndex", "2147483648"),
            ("runtimeIndex", "\"0\""), ("runtimeIndex", "null"),
            ("dacPath", "null"), ("dacPath", "false"), ("dacPath", "\"\""),
            ("dacPath", "\"relative.dll\""), ("processId", "1"),
            ("sourceFileMap", "{}"), ("sourceLinkOptions", "{}"), ("symbolOptions", "{}"),
            ("requireExactSource", "true"), ("justMyCode", "false"), ("enableStepFiltering", "false")
        ];
        foreach ((string name, string value) in invalidProperties)
        {
            using var invalid = JsonDocument.Parse(value);
            JsonElement failure = await RequestAsync(client, "attach", writer =>
            {
                writer.WriteStartObject();
                if (name != "dumpPath")
                {
                    writer.WriteString("dumpPath", fixture.DumpPath);
                }
                writer.WritePropertyName(name);
                invalid.RootElement.WriteTo(writer);
                writer.WriteEndObject();
            }, success: false).ConfigureAwait(false);
            string? message = failure.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains(name, message);
        }

        int stoppedThread = await OpenDumpAsync(client, fixture.DumpPath, initialize: false).ConfigureAwait(false);
        JsonElement threads = await RequestAsync(client, "threads", WriteEmptyObject).ConfigureAwait(false);
        Assert.Contains(stoppedThread, threads.GetProperty("threads").EnumerateArray()
            .Select(thread => thread.GetProperty("id").GetInt32()));
        _ = await RequestAsync(client, "disconnect", WriteEmptyObject).ConfigureAwait(false);
        using JsonDocument terminated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertEvent(terminated.RootElement, "terminated");
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    /// <summary>
    /// Inspects a terminated target with stable bounded pages and closes the dump session through either client exit path.
    /// </summary>
    /// <param name="disconnect">Whether to request disconnect or close the protocol input.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ReadOnlyDumpPreservesInspectionAndClosesSession(bool disconnect)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        DapTestClient client = await CreateClientAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        int threadId = await OpenDumpAsync(client, fixture.DumpPath).ConfigureAwait(false);
        Assert.IsGreaterThan(0, fixture.ProcessId);
        Assert.IsNull(client.TargetProcessId, "A dump must not advertise a historical PID as a live process.");

        JsonElement threads = await RequestAsync(client, "threads", WriteEmptyObject).ConfigureAwait(false);
        int[] threadIds = [.. threads.GetProperty("threads").EnumerateArray().Select(item => item.GetProperty("id").GetInt32())];
        Assert.Contains(threadId, threadIds);
        Assert.HasCount(threadIds.Length, threadIds.Distinct());
        JsonElement stack = await ReadStackAsync(client, threadId, start: 0, levels: 2).ConfigureAwait(false);
        int totalFrames = stack.GetProperty("totalFrames").GetInt32();
        Assert.IsGreaterThan(0, totalFrames);
        JsonElement frames = stack.GetProperty("stackFrames");
        Assert.AreEqual(Math.Min(2, totalFrames), frames.GetArrayLength());
        int firstFrame = frames[0].GetProperty("id").GetInt32();
        Assert.IsGreaterThan(0, firstFrame);
        Assert.IsFalse(string.IsNullOrWhiteSpace(frames[0].GetProperty("name").GetString()));
        Assert.IsFalse(frames[0].TryGetProperty("source", out _));
        JsonElement repeated = await ReadStackAsync(client, threadId, start: 0, levels: 1).ConfigureAwait(false);
        Assert.AreEqual(firstFrame, repeated.GetProperty("stackFrames")[0].GetProperty("id").GetInt32());
        Assert.AreEqual(totalFrames, repeated.GetProperty("totalFrames").GetInt32());
        JsonElement end = await ReadStackAsync(client, threadId, totalFrames, levels: 1).ConfigureAwait(false);
        Assert.AreEqual(0, end.GetProperty("stackFrames").GetArrayLength());
        Assert.AreEqual(totalFrames, end.GetProperty("totalFrames").GetInt32());

        JsonElement modules = await RequestAsync(client, "modules", WriteEmptyObject).ConfigureAwait(false);
        JsonElement moduleArray = modules.GetProperty("modules");
        int totalModules = modules.GetProperty("totalModules").GetInt32();
        Assert.AreEqual(moduleArray.GetArrayLength(), totalModules);
        Assert.Contains("csls-test-process-host.dll", moduleArray.EnumerateArray()
            .Select(module => module.GetProperty("name").GetString()));
        JsonElement modulePage = await RequestAsync(client, "modules", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("startModule", totalModules - 1);
            writer.WriteNumber("moduleCount", 1);
            writer.WriteEndObject();
        }).ConfigureAwait(false);
        Assert.AreEqual(1, modulePage.GetProperty("modules").GetArrayLength());
        Assert.AreEqual(moduleArray[totalModules - 1].GetProperty("id").GetInt32(),
            modulePage.GetProperty("modules")[0].GetProperty("id").GetInt32());
        Assert.AreEqual(totalModules, modulePage.GetProperty("totalModules").GetInt32());

        foreach (string command in new[] { "continue", "pause", "next", "stepIn", "stepOut", "restart",
            "setBreakpoints", "setExceptionBreakpoints", "evaluate", "setExpression", "setVariable",
            "exceptionInfo", "readMemory", "disassemble", "source" })
        {
            JsonElement failure = await RequestAsync(client, command, WriteEmptyObject, success: false).ConfigureAwait(false);
            string? message = failure.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("read-only dump", message);
        }

        JsonElement oversized = await RequestAsync(client, "stackTrace", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("threadId", threadId);
            writer.WriteNumber("levels", 4097);
            writer.WriteEndObject();
        }, success: false).ConfigureAwait(false);
        string? limitMessage = oversized.GetProperty("message").GetString();
        Assert.IsNotNull(limitMessage);
        Assert.Contains("4096", limitMessage);
        JsonElement recovered = await ReadStackAsync(client, threadId, start: 0, levels: 1).ConfigureAwait(false);
        Assert.AreEqual(firstFrame, recovered.GetProperty("stackFrames")[0].GetProperty("id").GetInt32());

        if (disconnect)
        {
            _ = await RequestAsync(client, "disconnect", writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("terminateDebuggee", true);
                writer.WriteEndObject();
            }).ConfigureAwait(false);
            using JsonDocument terminated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertEvent(terminated.RootElement, "terminated");
        }
        else
        {
            await client.CloseProtocolAsync().ConfigureAwait(false);
        }

        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        _ = await Assert.ThrowsExactlyAsync<EndOfStreamException>(async () =>
        {
            using JsonDocument unexpected = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        using FileStream releasedDump = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0L, releasedDump.Length);
    }

    private Task<DapTestClient> CreateClientAsync() => DapTestClient.CreateAsync(TestContext.CancellationToken,
        new Dictionary<string, string?>
        {
            ["CSLS_DEBUGGER_DUMP_WORKER_PATH"] = DumpWorkerPath
        });

    private static string DumpWorkerPath => Path.Join(FindRepositoryRoot(), "artifacts", "bin",
        "Csls.Debugger.Dump.Worker", "debug", "csls-debugger-dump-worker.dll");

    private async Task<int> OpenDumpAsync(DapTestClient client, string dumpPath, bool initialize = true,
        bool capabilitiesChanged = true)
    {
        if (initialize)
        {
            _ = await RequestAsync(client, "initialize", WriteEmptyObject).ConfigureAwait(false);
        }
        int attach = await PrepareDumpAsync(client, dumpPath, capabilitiesChanged: capabilitiesChanged)
            .ConfigureAwait(false);
        _ = await RequestAsync(client, "configurationDone", WriteEmptyObject).ConfigureAwait(false);
        using (JsonDocument attached = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(attached.RootElement, attach, "attach", success: true);
        }
        using JsonDocument stopped = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertEvent(stopped.RootElement, "stopped");
        Assert.AreEqual("dump", stopped.RootElement.GetProperty("body").GetProperty("reason").GetString());
        Assert.IsTrue(stopped.RootElement.GetProperty("body").GetProperty("allThreadsStopped").GetBoolean());
        return stopped.RootElement.GetProperty("body").GetProperty("threadId").GetInt32();
    }

    private async Task<int> PrepareDumpAsync(DapTestClient client, string dumpPath,
        bool capabilitiesChanged = true, int runtimeIndex = 0, string? dacPath = null)
    {
        int attach = await client.SendRequestAsync("attach", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("dumpPath", dumpPath);
            writer.WriteNumber("runtimeIndex", runtimeIndex);
            if (dacPath is not null)
            {
                writer.WriteString("dacPath", dacPath);
            }
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        if (capabilitiesChanged)
        {
            using JsonDocument capabilities = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertEvent(capabilities.RootElement, "capabilities");
            JsonElement body = capabilities.RootElement.GetProperty("body").GetProperty("capabilities");
            Assert.IsTrue(body.GetProperty("supportsModulesRequest").GetBoolean());
            Assert.IsTrue(body.GetProperty("supportsConfigurationDoneRequest").GetBoolean());
            Assert.IsTrue(body.GetProperty("supportsCancelRequest").GetBoolean());
            Assert.AreEqual(0, body.GetProperty("exceptionBreakpointFilters").GetArrayLength());
            foreach (JsonProperty capability in body.EnumerateObject().Where(property => property.Value.ValueKind == JsonValueKind.True))
            {
                Assert.Contains(capability.Name, s_dumpCapabilities);
            }
        }
        using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertEvent(initialized.RootElement, "initialized");
        }

        return attach;
    }

    private async Task CloseDumpAsync(DapTestClient client)
    {
        _ = await RequestAsync(client, "disconnect", WriteEmptyObject).ConfigureAwait(false);
        using JsonDocument terminated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertEvent(terminated.RootElement, "terminated");
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    private async Task<JsonElement> RequestAsync(DapTestClient client, string command,
        Action<Utf8JsonWriter> writeArguments, bool success = true)
    {
        int sequence = await client.SendRequestAsync(command, writeArguments, TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, command, success);
        return success && response.RootElement.TryGetProperty("body", out JsonElement body)
            ? body.Clone() : response.RootElement.Clone();
    }

    private Task<JsonElement> ReadStackAsync(DapTestClient client, int threadId, int start, int levels) =>
        RequestAsync(client, "stackTrace", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("threadId", threadId);
            writer.WriteNumber("startFrame", start);
            writer.WriteNumber("levels", levels);
            writer.WriteEndObject();
        });

    private async Task<JsonElement> ReadDumpVariablesAsync(DapTestClient client, int reference, int start, int count,
        string filter = "named", bool success = true)
    {
        JsonElement response = await RequestAsync(client, "variables", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("variablesReference", reference);
            writer.WriteNumber("start", start);
            writer.WriteNumber("count", count);
            writer.WriteString("filter", filter);
            writer.WriteEndObject();
        }, success).ConfigureAwait(false);
        return success ? response.GetProperty("variables") : response;
    }
}
