using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies session-wide raw value presentation through real DAP inspection requests.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Selects physical fields or debugger presentation while preserving source expressions and paging.
    /// </summary>
    /// <param name="showRawValues">The explicit presentation setting or omission that selects the default.</param>
    /// <param name="attach">Whether the process is independently owned before debugger activation.</param>
    [TestMethod]
    [DataRow(null, false)]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(null, true)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RawValueOptionControlsPhysicalInspection(bool? showRawValues, bool attach)
    {
        string waitPath = Path.Join(Path.GetTempPath(), $"csls-raw-values-{Guid.NewGuid():N}.signal");
        using Process? target = attach ? StartRawValueTarget(waitPath) : null;
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
                ? await StartStoppedFixtureAsync(waitPath, showRawValues: showRawValues).ConfigureAwait(false)
                : await AttachRawValueTargetAsync(target.Id, showRawValues).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            using DapTestCancellationCapture cancellationLog = CaptureProtocolOnCancellation(client);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int sequence = await client.SendRequestAsync("scopes",
                writer => WriteFrameArguments(writer, frame.GetProperty("id").GetInt32()),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument scopes = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertResponse(scopes.RootElement, sequence, "scopes", success: true);
            JsonElement locals = scopes.RootElement.GetProperty("body").GetProperty("scopes").EnumerateArray()
                .Single(scope => scope.GetProperty("name").GetString() == "Locals");
            JsonElement[] values = await ReadVariablesAsync(client, locals.GetProperty("variablesReference").GetInt32())
                .ConfigureAwait(false);
            bool raw = showRawValues == true;

            JsonElement display = FindByEvaluateName(values, "localDisplay");
            Assert.AreEqual(raw ? "{Csls.TestProcessHost.DebuggerDisplayFixture}" : "{id}=54; label=alpha\\nbeta; nested=55",
                display.GetProperty("value").GetString());
            Assert.AreEqual(raw ? "Csls.TestProcessHost.DebuggerDisplayFixture" : "display-7", display.GetProperty("type").GetString());
            JsonElement[] array = await ReadVariablesAsync(client,
                FindByEvaluateName(values, "localDisplayArray").GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            JsonElement element = Assert.ContainsSingle(array);
            Assert.AreEqual(raw ? "[0]" : "child-54", element.GetProperty("name").GetString());
            Assert.AreEqual("localDisplayArray[0]", element.GetProperty("evaluateName").GetString());
            Assert.AreEqual(display.GetProperty("value").GetString(), element.GetProperty("value").GetString());

            JsonElement[] members = await ReadVariablesAsync(client,
                FindByEvaluateName(values, "localDisplays").GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            JsonElement primitive = FindByEvaluateName(members, "localDisplays._memberPrimitive");
            Assert.AreEqual(raw ? "_memberPrimitive" : "member-number", primitive.GetProperty("name").GetString());
            Assert.AreEqual(raw ? "73" : "member-int=54", primitive.GetProperty("value").GetString());
            Assert.AreEqual(raw ? "int" : "member-int-type", primitive.GetProperty("type").GetString());
            Assert.AreEqual("0", FindByEvaluateName(members, "localDisplays._memberDisplayAccessCount").GetProperty("value").GetString());

            int browsableReference = FindByEvaluateName(values, "localBrowsable").GetProperty("variablesReference").GetInt32();
            JsonElement[] browsable = await ReadVariablesAsync(client, browsableReference).ConfigureAwait(false);
            Assert.AreSequenceEqual(raw ? s_rawDebuggerBrowsableNames : s_defaultDebuggerBrowsableNames,
                browsable.Select(value => value.GetProperty("name").GetString()));
            JsonElement[] page = await ReadVariablesPageAsync(client, browsableReference, start: 1, count: 2).ConfigureAwait(false);
            Assert.AreSequenceEqual(browsable.Skip(1).Take(2).Select(value => value.GetProperty("name").GetString()),
                page.Select(value => value.GetProperty("name").GetString()));
            if (raw)
            {
                Assert.AreEqual("47", FindByEvaluateName(browsable, "localBrowsable._hidden").GetProperty("value").GetString());
                Assert.IsEmpty(await ReadVariablesPageAsync(client, browsableReference, start: 0, count: 0, filter: "indexed")
                    .ConfigureAwait(false));
                await AssertRawTupleAndProxyAsync(client, values).ConfigureAwait(false);
                await AssertRawValueAssignmentAsync(client, browsable, frame.GetProperty("id").GetInt32()).ConfigureAwait(false);
                await ContinueAndPauseAsync(client).ConfigureAwait(false);
                int stale = await client.SendRequestAsync("variables", writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("variablesReference", browsableReference);
                    writer.WriteEndObject();
                }, TestContext.CancellationToken).ConfigureAwait(false);
                using JsonDocument retired = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
                AssertResponse(retired.RootElement, stale, "variables", success: false);
                JsonElement currentFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
                JsonElement currentDisplay = await ReadEvaluationAsync(client, currentFrame.GetProperty("id").GetInt32(),
                    "localDisplay", success: true, TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("{Csls.TestProcessHost.DebuggerDisplayFixture}",
                    currentDisplay.GetProperty("result").GetString());
            }

            await ResumeAndReleaseFixtureAsync(client, waitPath).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            if (target is not null && !target.HasExited)
            {
                target.Kill(entireProcessTree: true);
                await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            File.Delete(waitPath);
        }
    }

    private async Task AssertRawValueAssignmentAsync(DapTestClient client, JsonElement[] fields, int frameId)
    {
        int reference = FindByEvaluateName(fields, "localBrowsable._rootItems").GetProperty("variablesReference").GetInt32();
        foreach (string value in new[] { "99", "49" })
        {
            JsonElement assigned = await ReadSetVariableAsync(client, reference, "[0]", value,
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(value, assigned.GetProperty("value").GetString());
            JsonElement evaluated = await ReadEvaluationAsync(client, frameId, "localBrowsable._rootItems[0]",
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(value, evaluated.GetProperty("result").GetString());
        }
    }

    private static Process StartRawValueTarget(string waitPath)
    {
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add(ResolveTestProcessHost());
        startInfo.ArgumentList.Add("--debugger-authorized-fixture");
        startInfo.ArgumentList.Add(waitPath);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        return Process.Start(startInfo) ?? throw new AssertFailedException("The raw inspection target did not start.");
    }

    private async Task<DapTestClient> AttachRawValueTargetAsync(int processId, bool? showRawValues,
        bool? allowImplicitFuncEval = null)
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        try
        {
            int initialize = await client.SendRequestAsync("initialize", WriteVariablePagingInitializeArguments,
                TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, initialize, "initialize", success: true);
            }
            int attach = await client.SendRequestAsync("attach", writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("processId", processId);
                WriteDefaultSourceFileMap(writer);
                if (showRawValues.HasValue || allowImplicitFuncEval.HasValue)
                {
                    writer.WriteStartObject("expressionEvaluationOptions");
                    if (showRawValues is bool raw)
                    {
                        writer.WriteBoolean("showRawValues", raw);
                    }
                    if (allowImplicitFuncEval is bool implicitEvaluation)
                    {
                        writer.WriteBoolean("allowImplicitFuncEval", implicitEvaluation);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertEvent(initialized.RootElement, "initialized");
            }
            int configuration = await client.SendRequestAsync("configurationDone", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, configuration, "configurationDone", success: true);
            }
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, attach, "attach", success: true);
            }
            using (JsonDocument process = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertEvent(process.RootElement, "process");
                Assert.AreEqual(processId, process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32());
            }
            await PauseFixtureAsync(client).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task AssertRawTupleAndProxyAsync(DapTestClient client, JsonElement[] values)
    {
        JsonElement[] tuple = await ReadVariablesAsync(client,
            FindByEvaluateName(values, "localLongTuple").GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
        Assert.AreSequenceEqual(["Item1", "Item2", "Item3", "Item4", "Item5", "Item6", "Item7", "Rest"],
            tuple.Select(value => value.GetProperty("name").GetString()));
        JsonElement rest = FindByEvaluateName(tuple, "localLongTuple.Rest");
        JsonElement[] restFields = await ReadVariablesAsync(client, rest.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
        Assert.AreSequenceEqual(["Item1", "Item2"], restFields.Select(value => value.GetProperty("name").GetString()));
        Assert.AreSequenceEqual(["8", "9"], restFields.Select(value => value.GetProperty("value").GetString()));
        Assert.AreEqual("localLongTuple.Rest.Item1", restFields[0].GetProperty("evaluateName").GetString());

        JsonElement proxy = Assert.ContainsSingle(await ReadVariablesAsync(client,
            FindByEvaluateName(values, "localProxy").GetProperty("variablesReference").GetInt32()).ConfigureAwait(false));
        Assert.AreEqual("_rawValue", proxy.GetProperty("name").GetString());
        Assert.AreEqual("41", proxy.GetProperty("value").GetString());
        Assert.AreEqual("localProxy._rawValue", proxy.GetProperty("evaluateName").GetString());
        JsonElement[] enumerable = await ReadVariablesAsync(client,
            FindByEvaluateName(values, "localResultsView").GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
        Assert.DoesNotContain("Results View", enumerable.Select(value => value.GetProperty("name").GetString()));
        Assert.AreEqual("0", FindByEvaluateName(enumerable, "localResultsView._enumerationCount").GetProperty("value").GetString());
    }

    /// <summary>
    /// Rejects malformed presentation options without consuming the initialized adapter.
    /// </summary>
    /// <param name="option">The hostile JSON option passed through the real DAP transport.</param>
    [TestMethod]
    [DataRow("null")]
    [DataRow("true")]
    [DataRow("[]")]
    [DataRow("{\"showRawValues\":null}")]
    [DataRow("{\"showRawValues\":\"true\"}")]
    [DataRow("{\"showRawValues\":1}")]
    [DataRow("{\"showRawValues\":[]}")]
    [DataRow("{\"showRawValues\":{}}")]
    [DataRow("{\"unknownOption\":true}")]
    [DataRow("{\"showRawValues\":true,\"showRawValues\":false}")]
    [DataRow("{\"allowImplicitFuncEval\":null}")]
    [DataRow("{\"allowImplicitFuncEval\":\"true\"}")]
    [DataRow("{\"allowImplicitFuncEval\":1}")]
    [DataRow("{\"allowImplicitFuncEval\":[]}")]
    [DataRow("{\"allowImplicitFuncEval\":{}}")]
    [DataRow("{\"allowImplicitFuncEval\":true,\"showRawValues\":false,\"allowImplicitFuncEval\":false}")]
    [DataRow("{\"AllowImplicitFuncEval\":false}")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task InvalidExpressionEvaluationOptionsPreserveInitializedSession(string option)
    {
        foreach (string command in new[] { "launch", "attach" })
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            int initialize = await client.SendRequestAsync("initialize", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, initialize, "initialize", success: true);
            }
            int sequence = await client.SendRequestAsync(command, writer =>
            {
                writer.WriteStartObject();
                if (command == "attach")
                {
                    writer.WriteNumber("processId", Environment.ProcessId);
                }
                else
                {
                    writer.WriteString("program", ResolveTestProcessHost());
                }
                writer.WritePropertyName("expressionEvaluationOptions");
                writer.WriteRawValue(option);
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, sequence, command, success: false);
                Assert.Contains("expressionEvaluationOptions", response.RootElement.GetProperty("message").GetString()!);
            }
            _ = await LaunchAtEntryAsync(client, ResolveTestProcessHost(), [], initializeClient: false, writeProperties: writer =>
            {
                writer.WriteStartObject("expressionEvaluationOptions");
                writer.WriteEndObject();
            }).ConfigureAwait(false);
            await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
    }
}
