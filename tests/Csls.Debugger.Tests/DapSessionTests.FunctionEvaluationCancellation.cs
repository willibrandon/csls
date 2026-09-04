using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies cancellation and recovery for target-code evaluation through real DAP processes.
/// </summary>
public sealed partial class DapSessionTests
{
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
            await WaitForSignalAsync(waitPath + ".evaluation").ConfigureAwait(false);
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
            using JsonDocument concurrentEvaluation = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                concurrentEvaluation.RootElement,
                concurrentEvaluationSequence,
                "evaluate",
                success: false);
            Assert.Contains(
                "still in progress",
                concurrentEvaluation.RootElement.GetProperty("message").GetString()!,
                StringComparison.OrdinalIgnoreCase);

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
            await WaitForSignalAsync(waitPath + ".evaluation").ConfigureAwait(false);
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
            using JsonDocument concurrentAssignment = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                concurrentAssignment.RootElement,
                concurrentAssignmentSequence,
                "evaluate",
                success: false);
            Assert.Contains(
                "still in progress",
                concurrentAssignment.RootElement.GetProperty("message").GetString()!,
                StringComparison.OrdinalIgnoreCase);

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
        finally
        {
            File.Delete(waitPath);
            File.Delete(waitPath + ".evaluation");
        }
    }
}
