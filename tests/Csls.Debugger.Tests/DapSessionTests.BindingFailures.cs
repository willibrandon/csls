using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies binding failures reported after the runtime compiles a source method.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Publishes rejected optimized source locations and preserves another breakpoint in the same process.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task FailedSourceBindingBecomesUnverifiedAndPreservesSession()
    {
        string repositoryRoot = FindRepositoryRoot();
        string failedSource = Path.Join(repositoryRoot, "tests", "Csls.TestProcessHost", "ReferenceAssignmentFixture.cs");
        string validSource = Path.Join(repositoryRoot, "tests", "Csls.TestProcessHost", "DebuggerFixture.cs");
        int failedLine = FindSourceLine(await File.ReadAllLinesAsync(failedSource, TestContext.CancellationToken)
            .ConfigureAwait(false), "int result = DebuggerFixture.WaitForSignal(");
        int validLine = FindSourceLine(await File.ReadAllLinesAsync(validSource, TestContext.CancellationToken)
            .ConfigureAwait(false), "int localNumber = number + 1;");
        string program = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..",
            "Csls.TestProcessHost", "release", "csls-test-process-host.dll"));
        string signal = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, initialize, "initialize", success: true);
            }

            int launch = await client.SendRequestAsync("launch", writer => WriteLaunchArguments(writer,
                program, ["--debugger-reference-assignment-fixture", signal], wait: true,
                noDebug: false, suppressJitOptimizations: true), TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertEvent(initialized.RootElement, "initialized");
            }

            int failedId = await SetPendingBindingAsync(client, failedSource, failedLine).ConfigureAwait(false);
            int validId = await SetPendingBindingAsync(client, validSource, validLine).ConfigureAwait(false);
            Assert.AreNotEqual(failedId, validId);
            int configuration = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            bool launchReceived = false;
            bool configurationReceived = false;
            bool provisionallyBound = false;
            bool failureReceived = false;
            bool validBound = false;
            int threadId = 0;
            while (!launchReceived || !configurationReceived || !failureReceived || !validBound || threadId == 0)
            {
                using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
                JsonElement root = message.RootElement;
                if (root.GetProperty("type").GetString() == "response")
                {
                    int sequence = root.GetProperty("request_seq").GetInt32();
                    if (sequence == launch)
                    {
                        AssertResponse(root, launch, "launch", success: true);
                        launchReceived = true;
                    }
                    else
                    {
                        AssertResponse(root, configuration, "configurationDone", success: true);
                        configurationReceived = true;
                    }
                    continue;
                }

                string? eventName = root.GetProperty("event").GetString();
                if (eventName == "breakpoint")
                {
                    JsonElement body = root.GetProperty("body");
                    Assert.AreEqual("changed", body.GetProperty("reason").GetString());
                    JsonElement breakpoint = body.GetProperty("breakpoint");
                    int id = breakpoint.GetProperty("id").GetInt32();
                    bool verified = breakpoint.GetProperty("verified").GetBoolean();
                    if (id == failedId)
                    {
                        Assert.AreEqual(failedLine, breakpoint.GetProperty("line").GetInt32());
                        if (verified)
                        {
                            Assert.IsFalse(failureReceived);
                            provisionallyBound = true;
                        }
                        else
                        {
                            Assert.IsTrue(provisionallyBound);
                            Assert.IsFalse(failureReceived);
                            Assert.Contains("executable instruction", breakpoint.GetProperty("message").GetString()!);
                            failureReceived = true;
                        }
                    }
                    else
                    {
                        Assert.AreEqual(validId, id);
                        Assert.AreEqual(validLine, breakpoint.GetProperty("line").GetInt32());
                        Assert.IsTrue(verified);
                        validBound = true;
                    }
                }
                else if (eventName == "stopped")
                {
                    Assert.IsTrue(failureReceived);
                    JsonElement body = root.GetProperty("body");
                    Assert.AreEqual("breakpoint", body.GetProperty("reason").GetString());
                    threadId = body.GetProperty("threadId").GetInt32();
                    Assert.IsGreaterThan(0, threadId);
                }
                else
                {
                    Assert.IsTrue(eventName is "process" or "thread" or "module" or "output", root.GetRawText());
                }
            }

            JsonElement frame = await ReadTopSourceFrameAsync(client, threadId).ConfigureAwait(false);
            Assert.AreEqual(validLine, frame.GetProperty("line").GetInt32());
            Assert.IsTrue(DebuggerTestPath.AreEquivalent(validSource, frame.GetProperty("source").GetProperty("path").GetString()));
            JsonElement number = await ReadEvaluationAsync(client, frame.GetProperty("id").GetInt32(),
                "number", success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("42", number.GetProperty("result").GetString());
            int executableLine = FindSourceLine(await File.ReadAllLinesAsync(failedSource, TestContext.CancellationToken)
                .ConfigureAwait(false), "TBase genericTarget = genericBase;");
            int replacement = await client.SendRequestAsync("setBreakpoints",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteStartObject("source");
                    writer.WriteString("path", failedSource);
                    writer.WriteEndObject();
                    writer.WriteStartArray("breakpoints");
                    foreach (int line in new[] { failedLine, executableLine })
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("line", line);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, replacement, "setBreakpoints", success: true);
                JsonElement[] breakpoints = [.. response.RootElement.GetProperty("body").GetProperty("breakpoints").EnumerateArray()];
                Assert.HasCount(2, breakpoints);
                JsonElement breakpoint = breakpoints[0];
                Assert.IsFalse(breakpoint.GetProperty("verified").GetBoolean());
                Assert.AreEqual(failedLine, breakpoint.GetProperty("line").GetInt32());
                Assert.Contains("executable instruction", breakpoint.GetProperty("message").GetString()!);
                Assert.IsTrue(breakpoints[1].GetProperty("verified").GetBoolean());
                Assert.AreEqual(executableLine, breakpoints[1].GetProperty("line").GetInt32());
                Assert.AreNotEqual(breakpoint.GetProperty("id").GetInt32(), breakpoints[1].GetProperty("id").GetInt32());
            }

            JsonElement refreshedFrame = await ReadTopSourceFrameAsync(client, threadId).ConfigureAwait(false);
            Assert.AreEqual(frame.GetProperty("id").GetInt32(), refreshedFrame.GetProperty("id").GetInt32());
            await DisconnectAsync(client).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(signal);
        }
    }

    private async Task<int> SetPendingBindingAsync(DapTestClient client, string source, int line)
    {
        int sequence = await client.SendRequestAsync("setBreakpoints",
            writer => WriteSourceBreakpointArguments(writer, source, line), TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "setBreakpoints", success: true);
        JsonElement breakpoint = Assert.ContainsSingle(response.RootElement.GetProperty("body").GetProperty("breakpoints").EnumerateArray());
        Assert.IsFalse(breakpoint.GetProperty("verified").GetBoolean());
        Assert.AreEqual(line, breakpoint.GetProperty("line").GetInt32());
        return breakpoint.GetProperty("id").GetInt32();
    }
}
