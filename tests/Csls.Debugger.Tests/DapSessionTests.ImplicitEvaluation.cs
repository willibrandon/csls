using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies automatic presentation policy independently from deliberate target execution.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Applies launch and attach policy to real proxy side effects while preserving pure display and explicit calls.
    /// </summary>
    /// <param name="allowImplicitFuncEval">The explicit automatic evaluation policy or its default.</param>
    /// <param name="attach">Whether the target exists before debugger activation.</param>
    [TestMethod]
    [DataRow(null, false)]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(null, true)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ImplicitEvaluationOptionControlsProxySideEffects(bool? allowImplicitFuncEval, bool attach)
    {
        string signal = Path.Join(Path.GetTempPath(), $"csls-implicit-evaluation-{Guid.NewGuid():N}.signal");
        using Process? target = attach ? StartRawValueTarget(signal) : null;
        try
        {
            if (target is not null)
            {
                char[] ready = new char[5];
                Assert.AreEqual(ready.Length,
                    await target.StandardOutput.ReadBlockAsync(ready, TestContext.CancellationToken).ConfigureAwait(false));
                Assert.AreEqual("ready", new string(ready));
            }
            DapTestClient client = target is null
                ? await StartStoppedFixtureAsync(signal, allowImplicitFuncEval: allowImplicitFuncEval).ConfigureAwait(false)
                : await AttachRawValueTargetAsync(target.Id, showRawValues: null, allowImplicitFuncEval).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            using DapTestCancellationCapture diagnostics = CaptureProtocolOnCancellation(client);
            await ReachImplicitEvaluationBreakpointAsync(client).ConfigureAwait(false);
            await AssertProxyCounterAsync(client, "Constructions", "1").ConfigureAwait(false);
            await AssertProxyCounterAsync(client, "GetterCalls", "0").ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement proxy = await ReadEvaluationAsync(client, frame.GetProperty("id").GetInt32(),
                "localProxy", success: true, TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement[] fields = await ReadVariablesAsync(client, proxy.GetProperty("variablesReference").GetInt32())
                .ConfigureAwait(false);
            bool automatic = allowImplicitFuncEval != false;
            if (automatic)
            {
                Assert.AreEqual("52", fields.Single(field => field.GetProperty("name").GetString() == "ComputedValue")
                    .GetProperty("value").GetString());
                Assert.Contains("Raw View", fields.Select(field => field.GetProperty("name").GetString()));
                using JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
                AssertEvent(invalidated.RootElement, "invalidated");
            }
            else
            {
                JsonElement field = Assert.ContainsSingle(fields);
                Assert.AreEqual("_rawValue", field.GetProperty("name").GetString());
                Assert.AreEqual("41", field.GetProperty("value").GetString());
                Assert.AreEqual("localProxy._rawValue", field.GetProperty("evaluateName").GetString());
            }
            await AssertProxyCounterAsync(client, "Constructions", automatic ? "2" : "1").ConfigureAwait(false);
            await AssertProxyCounterAsync(client, "GetterCalls", automatic ? "1" : "0").ConfigureAwait(false);
            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement display = await ReadEvaluationAsync(client, frame.GetProperty("id").GetInt32(),
                "localDisplay", success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("{id}=54; label=alpha\\nbeta; nested=55", display.GetProperty("result").GetString());
            JsonElement resultsView = await ReadResultsViewRowAsync(client, "localResultsView").ConfigureAwait(false);
            await AssertEnumerationCountAsync(client, "localResultsView", 0).ConfigureAwait(false);
            JsonElement[] items = await ExpandResultsViewAsync(client, resultsView.GetProperty("variablesReference").GetInt32())
                .ConfigureAwait(false);
            Assert.AreSequenceEqual(["71", "72", "73"], items.Select(item => item.GetProperty("value").GetString()));
            await AssertEnumerationCountAsync(client, "localResultsView", 1).ConfigureAwait(false);
            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement executed = await ReadEvaluationAsync(client, frame.GetProperty("id").GetInt32(),
                "localObject.NextNumber()", success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("43", executed.GetProperty("result").GetString());
            using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertEvent(invalidated.RootElement, "invalidated");
            }
            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement changed = await ReadEvaluationAsync(client, frame.GetProperty("id").GetInt32(),
                "localObject.Number", success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("42", changed.GetProperty("result").GetString());
            await ResumeAndReleaseFixtureAsync(client, signal).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            if (target is not null && !target.HasExited)
            {
                target.Kill(entireProcessTree: true);
                await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            File.Delete(signal);
        }
    }

    private async Task AssertProxyCounterAsync(DapTestClient client, string field, string expected)
    {
        JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
        JsonElement result = await ReadEvaluationAsync(client, frame.GetProperty("id").GetInt32(),
            $"localProxyCounters.{field}",
            success: true, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(expected, result.GetProperty("result").GetString(), field);
    }

    private async Task ReachImplicitEvaluationBreakpointAsync(DapTestClient client)
    {
        string source = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "DebuggerFixture.cs");
        int line = FindSourceLine(await File.ReadAllLinesAsync(source, TestContext.CancellationToken).ConfigureAwait(false),
            "Thread.Sleep(1);");
        int breakpoint = await client.SendRequestAsync("setBreakpoints",
            writer => WriteSourceBreakpointArguments(writer, source, line), TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, breakpoint, "setBreakpoints", success: true);
            Assert.IsTrue(Assert.ContainsSingle(response.RootElement.GetProperty("body").GetProperty("breakpoints")
                .EnumerateArray()).GetProperty("verified").GetBoolean());
        }
        int resume = await client.SendRequestAsync("continue", WriteEmptyObject, TestContext.CancellationToken).ConfigureAwait(false);
        bool acknowledged = false;
        bool continued = false;
        bool stopped = false;
        while (!acknowledged || !continued || !stopped)
        {
            using JsonDocument message = await ReadExecutionControlMessageAsync(client).ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                AssertResponse(root, resume, "continue", success: true);
                acknowledged = true;
            }
            else if (root.GetProperty("event").GetString() == "continued")
            {
                continued = true;
            }
            else
            {
                AssertEvent(root, "stopped");
                Assert.AreEqual("breakpoint", root.GetProperty("body").GetProperty("reason").GetString());
                stopped = true;
            }
        }
        int clear = await client.SendRequestAsync("setBreakpoints",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartObject("source");
                writer.WriteString("path", source);
                writer.WriteEndObject();
                writer.WriteStartArray("breakpoints");
                writer.WriteEndArray();
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument cleared = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(cleared.RootElement, clear, "setBreakpoints", success: true);
        Assert.IsEmpty(cleared.RootElement.GetProperty("body").GetProperty("breakpoints").EnumerateArray());
    }
}
