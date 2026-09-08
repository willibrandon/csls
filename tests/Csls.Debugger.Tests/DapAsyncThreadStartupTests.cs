using Microsoft.Diagnostics.NETCore.Client;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies managed thread startup through the same DAP contract for csls and an explicit oracle.
/// </summary>
[TestClass]
public sealed class DapAsyncThreadStartupTests : DapTestContext
{
    private int _launchSequence;
    private bool _launchResponseReceived;

    /// <summary>
    /// Gets independently reported compiler shapes and thread-start allocation samples.
    /// </summary>
    public static IEnumerable<(string Configuration, string Kind, int Iteration)> StartupCases
    {
        get
        {
            foreach (string configuration in new[] { "Debug", "Release" })
            {
                foreach (string kind in new[] { "Task", "ValueTask" })
                {
                    for (int iteration = 0; iteration < 8; iteration++)
                    {
                        yield return (configuration, kind, iteration);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Starts independent targets and inspects the requested Task or ValueTask suspension on a new managed thread.
    /// </summary>
    /// <param name="configuration">The fixture compiler configuration.</param>
    /// <param name="kind">The asynchronous result shape.</param>
    /// <param name="iteration">The independently reported thread-start allocation sample.</param>
    [TestMethod]
    [DynamicData(nameof(StartupCases))]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task AsyncThreadStartupStopsAtRequestedSource(string configuration, string kind, int iteration)
    {
        string root = FindRepositoryRoot();
        string source = Path.Join(root, "tests", "Csls.TestProcessHost", "DebuggerAsyncStepOutFixture.cs");
        string[] lines = await File.ReadAllLinesAsync(source, TestContext.CancellationToken).ConfigureAwait(false);
        int line = FindSourceLine(lines, kind == "Task" ? "// task suspension" : "// value task suspension");
        string program = Path.Join(root, "artifacts", "bin", "Csls.TestProcessHost", configuration == "Release" ? "release" : "debug",
            "csls-test-process-host.dll");
        string? oracle = Environment.GetEnvironmentVariable("CSLS_DAP_ORACLE_PATH");
        string breakpointSource = oracle is null ? source : ReadRecordedSource(program, Path.GetFileName(source));
        TestContext.WriteLine($"Adapter: {oracle ?? "csls"}; fixture: {program}; shape: {kind}; iteration: {iteration}.");
        TestContext.WriteLine($"Breakpoint source: {breakpointSource}:{line}.");
        await VerifyStartupAsync(program, breakpointSource, line, kind, oracle).ConfigureAwait(false);
    }

    private static string ReadRecordedSource(string program, string fileName)
    {
        using FileStream stream = File.OpenRead(Path.ChangeExtension(program, ".pdb"));
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        MetadataReader reader = provider.GetMetadataReader();
        return Assert.ContainsSingle(reader.Documents.Select(handle => reader.GetString(reader.GetDocument(handle).Name))
            .Where(path => path.Replace('\\', '/').EndsWith("/" + fileName, StringComparison.Ordinal)));
    }

    private async Task VerifyStartupAsync(string program, string source, int line, string kind, string? oracle)
    {
        _launchSequence = 0;
        _launchResponseReceived = false;
        string pipeName = $"cs-{Guid.NewGuid():N}";
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        using var selected = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var competing = new NamedPipeServerStream(pipeName + "-c", PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var connections = Task.WhenAll(selected.WaitForConnectionAsync(connectionCancellation.Token),
            competing.WaitForConnectionAsync(connectionCancellation.Token));
        var reports = new DebuggerCrashReportCapture(TestContext, DumpType.WithHeap);
        await using ConfiguredAsyncDisposable reportCleanup = reports.ConfigureAwait(false);
        var environment = new Dictionary<string, string?>(reports.Variables)
        {
            ["GITHUB_TOKEN"] = null,
            ["GH_TOKEN"] = null
        };
        DapTestClient? client = null;
        try
        {
            client = oracle is null
                ? await DapTestClient.CreateAsync(TestContext.CancellationToken, environment).ConfigureAwait(false)
                : await DapTestClient.CreateOracleAsync(oracle, environment, TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
            using DapTestCancellationCapture cancellationLog = CaptureProtocolOnCancellation(client);
            int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken, writeProperties: writer =>
            {
                writer.WriteString("adapterID", "coreclr");
                writer.WriteString("pathFormat", "path");
                writer.WriteBoolean("linesStartAt1", true);
                writer.WriteBoolean("columnsStartAt1", true);
            }).ConfigureAwait(false);
            _ = await ReadResponseAsync(client, initialize).ConfigureAwait(false);
            _launchSequence = await client.SendRequestAsync("launch", writer => WriteLaunchArguments(writer, program,
                ["--debugger-concurrent-async-step-out-fixture", pipeName, kind], wait: true, noDebug: false,
                suppressJitOptimizations: true), TestContext.CancellationToken).ConfigureAwait(false);
            _ = await ReadEventAsync(client, "initialized").ConfigureAwait(false);
            int setBreakpoints = await client.SendRequestAsync("setBreakpoints", writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartObject("source");
                writer.WriteString("path", source);
                writer.WriteEndObject();
                writer.WriteStartArray("breakpoints");
                writer.WriteStartObject();
                writer.WriteNumber("line", line);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement binding = await ReadResponseAsync(client, setBreakpoints).ConfigureAwait(false);
            Assert.HasCount(1, binding.GetProperty("body").GetProperty("breakpoints").EnumerateArray());
            TestContext.WriteLine($"Initial source binding: {binding.GetRawText()}.");
            int configured = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement stopped = await ReadStopAsync(client, configured).ConfigureAwait(false);
            await connections.ConfigureAwait(false);
            Assert.AreEqual("breakpoint", stopped.GetProperty("body").GetProperty("reason").GetString());
            int thread = stopped.GetProperty("body").GetProperty("threadId").GetInt32();
            Assert.IsGreaterThan(0, thread);
            int stack = await client.SendRequestAsync("stackTrace", writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("threadId", thread);
                writer.WriteNumber("startFrame", 0);
                writer.WriteNumber("levels", 1);
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement stackResponse = await ReadResponseAsync(client, stack).ConfigureAwait(false);
            JsonElement frame = Assert.ContainsSingle(stackResponse.GetProperty("body").GetProperty("stackFrames").EnumerateArray());
            Assert.AreEqual(line, frame.GetProperty("line").GetInt32());
            Assert.AreEqual(Path.GetFullPath(source), Path.GetFullPath(frame.GetProperty("source").GetProperty("path").GetString()!));
            string? method = frame.GetProperty("name").GetString();
            Assert.IsNotNull(method);
            Assert.Contains("Read" + kind + "Async", method);
            JsonElement answer = await ReadEvaluationAsync(client, frame.GetProperty("id").GetInt32(), "answer",
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("40", answer.GetProperty("result").GetString());
            int processId = client.TargetProcessId ?? throw new InvalidDataException("The adapter omitted the target identity.");
            using var target = Process.GetProcessById(processId);
            Assert.IsFalse(target.HasExited);
            int disconnect = await client.SendRequestAsync("disconnect", writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("terminateDebuggee", true);
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            _ = await ReadResponseAsync(client, disconnect).ConfigureAwait(false);
            await target.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await client.CloseProtocolAsync().ConfigureAwait(false);
            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        catch
        {
            TestContext.WriteLine(client?.ProtocolTranscript ?? "Adapter creation failed.");
            throw;
        }
        finally
        {
            await connectionCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await connections.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (connectionCancellation.IsCancellationRequested)
            {
                Debug.Assert(connections.IsCanceled);
            }
        }
    }

    private async Task<JsonElement> ReadResponseAsync(DapTestClient client, int sequence)
    {
        while (true)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement value = message.RootElement;
            if (value.GetProperty("type").GetString() == "response")
            {
                if (value.GetProperty("request_seq").GetInt32() == _launchSequence)
                {
                    ObserveLaunchResponse(value);
                    continue;
                }
                Assert.AreEqual(sequence, value.GetProperty("request_seq").GetInt32(), value.GetRawText());
                Assert.IsTrue(value.GetProperty("success").GetBoolean(), value.GetRawText());
                return value.Clone();
            }
        }
    }

    private async Task<JsonElement> ReadEventAsync(DapTestClient client, string name)
    {
        while (true)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement value = message.RootElement;
            if (value.GetProperty("type").GetString() == "response")
            {
                ObserveLaunchResponse(value);
                continue;
            }
            Assert.AreEqual("event", value.GetProperty("type").GetString(), value.GetRawText());
            if (value.GetProperty("event").GetString() == name)
            {
                return value.Clone();
            }
            Assert.IsFalse(value.GetProperty("event").GetString() is "exited" or "terminated", value.GetRawText());
        }
    }

    private void ObserveLaunchResponse(JsonElement value)
    {
        Assert.AreEqual(_launchSequence, value.GetProperty("request_seq").GetInt32(), value.GetRawText());
        Assert.IsTrue(value.GetProperty("success").GetBoolean(), value.GetRawText());
        Assert.IsFalse(_launchResponseReceived);
        _launchResponseReceived = true;
    }

    private async Task<JsonElement> ReadStopAsync(DapTestClient client, int configured)
    {
        var pending = new HashSet<int> { configured };
        if (!_launchResponseReceived)
        {
            pending.Add(_launchSequence);
        }
        JsonElement? stopped = null;
        while (pending.Count > 0 || stopped is null)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement value = message.RootElement;
            if (value.GetProperty("type").GetString() == "response")
            {
                Assert.IsTrue(pending.Remove(value.GetProperty("request_seq").GetInt32()), value.GetRawText());
                Assert.IsTrue(value.GetProperty("success").GetBoolean(), value.GetRawText());
            }
            else if (value.GetProperty("event").GetString() == "stopped")
            {
                Assert.IsNull(stopped);
                stopped = value.Clone();
            }
            else
            {
                Assert.IsFalse(value.GetProperty("event").GetString() is "exited" or "terminated", value.GetRawText());
            }
        }
        return stopped.Value;
    }
}
