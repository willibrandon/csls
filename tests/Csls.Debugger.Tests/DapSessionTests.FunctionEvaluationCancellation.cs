using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies cancellation and recovery for target-code evaluation through real DAP processes.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Reports evaluation deadlines and cancellation during cooperative abort while preserving inspection and later target calls.
    /// </summary>
    /// <param name="command">The target-code request whose deadline expires.</param>
    /// <param name="cancelDuringAbort">Whether to cancel explicitly after the deadline starts cooperative abort.</param>
    [TestMethod]
    [DataRow("evaluate", false)]
    [DataRow("setExpression", false)]
    [DataRow("setVariable", false)]
    [DataRow("evaluate", true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedEvaluationDeadlinePreservesSession(string command, bool cancelDuringAbort)
    {
        string waitPath = CreateResultsViewSignalPath();
        DapTestClient? diagnosticClient = null;
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            diagnosticClient = client;
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            (int localsReference, _) = await ReadLogicalFrameLocalsAsync(client, frameId).ConfigureAwait(false);
            int sequence = await client.SendRequestAsync(command, writer =>
            {
                string call = cancelDuringAbort
                    ? "localObject.WaitForDebuggerAbortRelease()"
                    : "localObject.WaitForDebuggerCancellation()";
                writer.WriteStartObject();
                if (command == "setVariable")
                {
                    writer.WriteNumber("variablesReference", localsReference);
                    writer.WriteString("name", "localNumber");
                }
                else
                {
                    writer.WriteNumber("frameId", frameId);
                    writer.WriteString("expression", command == "evaluate" ? call : "localNumber");
                }

                if (command != "evaluate")
                {
                    writer.WriteString("value", call);
                }

                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            await client.WaitForTargetSignalAsync(waitPath + ".evaluation", sequence, TestContext.CancellationToken)
                .ConfigureAwait(false);
            int inspection = await client.SendRequestAsync("threads", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            if (cancelDuringAbort)
            {
                await client.WaitForTargetSignalAsync(waitPath + ".evaluation.aborting", sequence,
                    TestContext.CancellationToken).ConfigureAwait(false);
                int cancellation = await SendRequestCancellationAsync(client, sequence).ConfigureAwait(false);
                using JsonDocument acknowledged = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
                AssertResponse(acknowledged.RootElement, cancellation, "cancel", success: true);
                await File.WriteAllTextAsync(waitPath + ".evaluation.release", "release", TestContext.CancellationToken)
                    .ConfigureAwait(false);
            }

            {
                using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
                AssertResponse(response.RootElement, sequence, command, success: false);
                Assert.Contains(cancelDuringAbort ? "cancelled" : "deadline", response.RootElement.GetProperty("message").GetString()!);
                using JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
                AssertEvent(invalidated.RootElement, "invalidated");
                Assert.AreSequenceEqual(["stacks", "variables"], invalidated.RootElement.GetProperty("body")
                    .GetProperty("areas").EnumerateArray().Select(area => area.GetString()).ToArray());
            }

            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, inspection, "threads", success: true);
                Assert.IsNotEmpty(response.RootElement.GetProperty("body").GetProperty("threads").EnumerateArray());
            }

            JsonElement currentFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            AssertSameLogicalFrame(frame, currentFrame);
            JsonElement number = await ReadEvaluationAsync(client, frameId, "localNumber", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("43", number.GetProperty("result").GetString());
            JsonElement evaluated = await ReadEvaluationAsync(client, frameId, "localObject.AddForDebugger(2)",
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("44", evaluated.GetProperty("result").GetString());
            using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertEvent(invalidated.RootElement, "invalidated");
            }

            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        catch
        {
            WriteTargetCodeFailureDiagnostics(diagnosticClient);
            throw;
        }
        finally
        {
            File.Delete(waitPath);
            File.Delete(waitPath + ".evaluation");
            File.Delete(waitPath + ".evaluation.aborting");
            File.Delete(waitPath + ".evaluation.release");
        }
    }

    /// <summary>
    /// Cancels one running method evaluation and preserves the stopped target for later requests.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedFunctionEvaluationCancelsMethodAndRecovers()
    {
        string sourcePath = Path.Join(
            FindRepositoryRoot(),
            "tests",
            "Csls.TestProcessHost",
            "DebuggerFixture.cs");
        int breakpointLine = FindSourceLine(
            await File.ReadAllLinesAsync(
                sourcePath,
                TestContext.CancellationToken).ConfigureAwait(false),
            "Console.Write(announcement);");
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-function-evaluation-cancel-{Guid.NewGuid():N}.signal");
        DapTestClient? diagnosticClient = null;
        try
        {
            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            diagnosticClient = client;
            await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
            _ = await client.SendInitializeRequestAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialize = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.IsTrue(
                initialize.RootElement
                    .GetProperty("body")
                    .GetProperty("supportsCancelRequest")
                    .GetBoolean());
            int launchSequence = await client.SendRequestAsync(
                "launch",
                writer => WriteLaunchArguments(
                    writer,
                    ResolveTestProcessHost(),
                    ["--debugger-fixture", waitPath],
                    wait: true,
                    noDebug: false,
                    suppressJitOptimizations: true),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialized = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(initialized.RootElement, "initialized");

            int breakpointSequence = await client.SendRequestAsync(
                "setBreakpoints",
                writer => WriteSourceBreakpointArguments(writer, sourcePath, breakpointLine),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument breakpointResponse = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                breakpointResponse.RootElement,
                breakpointSequence,
                "setBreakpoints",
                success: true);

            int configurationSequence = await client.SendRequestAsync(
                "configurationDone",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            await ReadFunctionEvaluationStopAsync(
                client,
                configurationSequence,
                launchSequence).ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int cancelableEvaluationSequence = await client.SendRequestAsync(
                "evaluate",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString(
                        "expression",
                        "localObject.WaitForDebuggerCancellation()");
                    writer.WriteNumber("frameId", frame.GetProperty("id").GetInt32());
                    writer.WriteString("context", "watch");
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            await client.WaitForTargetSignalAsync(
                waitPath + ".evaluation", cancelableEvaluationSequence, TestContext.CancellationToken)
                .ConfigureAwait(false);
            int concurrentEvaluationSequence = await client.SendRequestAsync(
                "evaluate",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("expression", "localObject.Number");
                    writer.WriteNumber("frameId", frame.GetProperty("id").GetInt32());
                    writer.WriteString("context", "watch");
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertQueuedRequestCanceledAsync(client, concurrentEvaluationSequence, "evaluate")
                .ConfigureAwait(false);

            int cancelSequence = await client.SendRequestAsync(
                "cancel",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("requestId", cancelableEvaluationSequence);
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertCanceledTargetCodeOperationAsync(
                client,
                cancelableEvaluationSequence,
                cancelSequence,
                "evaluate").ConfigureAwait(false);

            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement afterCancellation = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.Number",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("42", afterCancellation.GetProperty("result").GetString());

            await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                await client.WaitForExitAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        catch
        {
            WriteTargetCodeFailureDiagnostics(diagnosticClient);
            throw;
        }
        finally
        {
            File.Delete(waitPath);
            File.Delete(waitPath + ".evaluation");
        }
    }

    /// <summary>
    /// Cancels one assignment evaluation and preserves the stopped target for later requests.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedAssignmentEvaluationCancelsMethodAndRecovers()
    {
        string sourcePath = Path.Join(
            FindRepositoryRoot(),
            "tests",
            "Csls.TestProcessHost",
            "DebuggerFixture.cs");
        int breakpointLine = FindSourceLine(
            await File.ReadAllLinesAsync(
                sourcePath,
                TestContext.CancellationToken).ConfigureAwait(false),
            "Console.Write(announcement);");
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-assignment-evaluation-cancel-{Guid.NewGuid():N}.signal");
        DapTestClient? diagnosticClient = null;
        try
        {
            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            diagnosticClient = client;
            await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
            _ = await client.SendInitializeRequestAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialize = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.IsTrue(
                initialize.RootElement
                    .GetProperty("body")
                    .GetProperty("supportsCancelRequest")
                    .GetBoolean());
            int launchSequence = await client.SendRequestAsync(
                "launch",
                writer => WriteLaunchArguments(
                    writer,
                    ResolveTestProcessHost(),
                    ["--debugger-fixture", waitPath],
                    wait: true,
                    noDebug: false,
                    suppressJitOptimizations: true),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialized = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(initialized.RootElement, "initialized");

            int breakpointSequence = await client.SendRequestAsync(
                "setBreakpoints",
                writer => WriteSourceBreakpointArguments(writer, sourcePath, breakpointLine),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument breakpointResponse = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                breakpointResponse.RootElement,
                breakpointSequence,
                "setBreakpoints",
                success: true);

            int configurationSequence = await client.SendRequestAsync(
                "configurationDone",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            await ReadFunctionEvaluationStopAsync(
                client,
                configurationSequence,
                launchSequence).ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int cancelableAssignmentSequence = await client.SendRequestAsync(
                "setExpression",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("expression", "localNumber");
                    writer.WriteString(
                        "value",
                        "localObject.WaitForDebuggerCancellation()");
                    writer.WriteNumber("frameId", frame.GetProperty("id").GetInt32());
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            await client.WaitForTargetSignalAsync(
                waitPath + ".evaluation", cancelableAssignmentSequence, TestContext.CancellationToken)
                .ConfigureAwait(false);
            int concurrentAssignmentSequence = await client.SendRequestAsync(
                "evaluate",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("expression", "localNumber");
                    writer.WriteNumber("frameId", frame.GetProperty("id").GetInt32());
                    writer.WriteString("context", "watch");
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertQueuedRequestCanceledAsync(client, concurrentAssignmentSequence, "evaluate")
                .ConfigureAwait(false);

            int assignmentCancelSequence = await client.SendRequestAsync(
                "cancel",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("requestId", cancelableAssignmentSequence);
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertCanceledTargetCodeOperationAsync(
                client,
                cancelableAssignmentSequence,
                assignmentCancelSequence,
                "setExpression").ConfigureAwait(false);

            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement afterAssignmentCancellation = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localNumber",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("43", afterAssignmentCancellation.GetProperty("result").GetString());

            await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                await client.WaitForExitAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        catch
        {
            WriteTargetCodeFailureDiagnostics(diagnosticClient);
            throw;
        }
        finally
        {
            File.Delete(waitPath);
            File.Delete(waitPath + ".evaluation");
        }
    }

    private void WriteTargetCodeFailureDiagnostics(DapTestClient? client)
    {
        if (client is not null)
        {
            TestContext.WriteLine($"Adapter diagnostics: {client.Diagnostics}");
            TestContext.WriteLine($"Recent protocol messages:{Environment.NewLine}{client.ProtocolTranscript}");
        }
    }
}
