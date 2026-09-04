using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies explicitly authorized target-code evaluation through real DAP processes.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Invokes instance and static methods and restores the stopped target afterward.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedFunctionEvaluationInvokesMethodsAndRestoresTarget()
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
            $"csls-debugger-function-evaluation-{Guid.NewGuid():N}.signal");
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
            JsonElement evaluation = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.NextNumber()",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("43", evaluation.GetProperty("result").GetString());
            Assert.AreEqual("int", evaluation.GetProperty("type").GetString());

            using JsonDocument invalidated = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(invalidated.RootElement, "invalidated");
            string[] invalidatedAreas = [.. invalidated.RootElement
                .GetProperty("body")
                .GetProperty("areas")
                .EnumerateArray()
                .Select(static area => area.GetString()!)];
            Assert.Contains("stacks", invalidatedAreas);
            Assert.Contains("variables", invalidatedAreas);

            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement stringArgumentEvaluation = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.LengthForDebugger(localText)",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "7",
                stringArgumentEvaluation.GetProperty("result").GetString());
            using JsonDocument stringArgumentInvalidated = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(stringArgumentInvalidated.RootElement, "invalidated");

            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement literalStringArgument = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.LengthForDebugger(\"answer!\")",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "7",
                literalStringArgument.GetProperty("result").GetString());
            using JsonDocument literalStringInvalidated = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(literalStringInvalidated.RootElement, "invalidated");

            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement computedStringArgument = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.LengthForDebugger(\"answer\" + \"!\")",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "7",
                computedStringArgument.GetProperty("result").GetString());
            using JsonDocument computedStringInvalidated = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(computedStringInvalidated.RootElement, "invalidated");

            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement multipleStringArguments = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.CombinedLengthForDebugger(\"a\\0\", \"bc\")",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "4",
                multipleStringArguments.GetProperty("result").GetString());
            using JsonDocument multipleStringInvalidated = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(multipleStringInvalidated.RootElement, "invalidated");

            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement argumentEvaluation = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.AddForDebugger(localNumber - 42)",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("43", argumentEvaluation.GetProperty("result").GetString());
            using JsonDocument argumentInvalidated = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(argumentInvalidated.RootElement, "invalidated");

            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement intOverloadEvaluation = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.AddOverloadedForDebugger(1)",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "43",
                intOverloadEvaluation.GetProperty("result").GetString());
            using JsonDocument intOverloadInvalidated = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(intOverloadInvalidated.RootElement, "invalidated");

            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement longOverloadEvaluation = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.AddOverloadedForDebugger(1L)",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "143",
                longOverloadEvaluation.GetProperty("result").GetString());
            using JsonDocument longOverloadInvalidated = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(longOverloadInvalidated.RootElement, "invalidated");

            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement referenceArgumentEvaluation = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.IsSameForDebugger(localObject)",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "true",
                referenceArgumentEvaluation.GetProperty("result").GetString());
            using JsonDocument referenceArgumentInvalidated = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(referenceArgumentInvalidated.RootElement, "invalidated");

            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement inheritedMethodEvaluation = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.Equals(localObject)",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "true",
                inheritedMethodEvaluation.GetProperty("result").GetString());
            using JsonDocument inheritedMethodInvalidated = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(inheritedMethodInvalidated.RootElement, "invalidated");

            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement staticMethodEvaluation = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "System.Math.Abs(-42)",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "42",
                staticMethodEvaluation.GetProperty("result").GetString());
            Assert.AreEqual("int", staticMethodEvaluation.GetProperty("type").GetString());
            using JsonDocument staticMethodInvalidated = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(staticMethodInvalidated.RootElement, "invalidated");

            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement missingStaticType = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "Missing.Namespace.Type.Abs(-42)",
                success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains(
                "No loaded runtime type",
                missingStaticType.GetProperty("message").GetString()!,
                StringComparison.Ordinal);

            JsonElement nullArgumentEvaluation = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.IsNullForDebugger(null)",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "true",
                nullArgumentEvaluation.GetProperty("result").GetString());
            using JsonDocument nullArgumentInvalidated = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(nullArgumentInvalidated.RootElement, "invalidated");

            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement stillStopped = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.Number",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("42", stillStopped.GetProperty("result").GetString());

            JsonElement exceptionEvaluation = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.ThrowForDebugger()",
                success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains(
                "threw",
                exceptionEvaluation.GetProperty("message").GetString()!,
                StringComparison.OrdinalIgnoreCase);
            using JsonDocument exceptionInvalidated = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(exceptionInvalidated.RootElement, "invalidated");

            JsonElement afterException = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.Number",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("42", afterException.GetProperty("result").GetString());

            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int assignmentFrameId = frame.GetProperty("id").GetInt32();
            JsonElement assignedString = await ReadSetExpressionAsync(
                client,
                assignmentFrameId,
                "localObject.Text",
                "\"changed\"",
                success: true,
                TestContext.CancellationToken,
                targetCodeExecuted: true).ConfigureAwait(false);
            Assert.AreEqual(
                "\"changed\"",
                assignedString.GetProperty("value").GetString());
            JsonElement assignedStringThroughOriginalFrame = await ReadEvaluationAsync(
                client,
                assignmentFrameId,
                "localObject.Text",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("\"changed\"", assignedStringThroughOriginalFrame.GetProperty("result").GetString());
            Assert.AreEqual("string", assignedStringThroughOriginalFrame.GetProperty("type").GetString());
            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            Assert.AreEqual(assignmentFrameId, frame.GetProperty("id").GetInt32());
            JsonElement assignedStringValue = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.Text",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "\"changed\"",
                assignedStringValue.GetProperty("result").GetString());

            JsonElement assignedCallResult = await ReadSetExpressionAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localNumber",
                "localObject.AddForDebugger(8)",
                success: true,
                TestContext.CancellationToken,
                targetCodeExecuted: true).ConfigureAwait(false);
            Assert.AreEqual("50", assignedCallResult.GetProperty("value").GetString());
            Assert.AreEqual("int", assignedCallResult.GetProperty("type").GetString());

            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement assignedConstruction = await ReadSetExpressionAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject",
                "new Csls.TestProcessHost.DebuggerFixtureValue(7, \"built\", \"unused\")",
                success: true,
                TestContext.CancellationToken,
                targetCodeExecuted: true).ConfigureAwait(false);
            Assert.AreEqual(
                "Csls.TestProcessHost.DebuggerFixtureValue",
                assignedConstruction.GetProperty("type").GetString());
            Assert.IsGreaterThan(
                0,
                assignedConstruction.GetProperty("variablesReference").GetInt32());

            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement assignedObjectNumber = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.Number",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("7", assignedObjectNumber.GetProperty("result").GetString());
            JsonElement assignedObjectText = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.Text",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("\"built\"", assignedObjectText.GetProperty("result").GetString());

            int failedAssignmentFrameId = frame.GetProperty("id").GetInt32();
            JsonElement failedAssignment = await ReadSetExpressionAsync(
                client,
                failedAssignmentFrameId,
                "localNumber",
                "localObject.ThrowForDebugger()",
                success: false,
                TestContext.CancellationToken,
                targetCodeExecuted: true).ConfigureAwait(false);
            Assert.Contains(
                "threw",
                failedAssignment.GetProperty("message").GetString()!,
                StringComparison.OrdinalIgnoreCase);
            JsonElement valueThroughOriginalFrame = await ReadEvaluationAsync(
                client,
                failedAssignmentFrameId,
                "localNumber",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("50", valueThroughOriginalFrame.GetProperty("result").GetString());
            Assert.AreEqual("int", valueThroughOriginalFrame.GetProperty("type").GetString());
            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            Assert.AreEqual(failedAssignmentFrameId, frame.GetProperty("id").GetInt32());
            JsonElement valueAfterFailedAssignment = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localNumber",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "50",
                valueAfterFailedAssignment.GetProperty("result").GetString());

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

    private async Task AssertCanceledTargetCodeOperationAsync(
        DapTestClient client,
        int operationSequence,
        int cancelSequence,
        string command)
    {
        bool evaluationReceived = false;
        bool cancelReceived = false;
        bool invalidatedReceived = false;
        while (!evaluationReceived || !cancelReceived || !invalidatedReceived)
        {
            using JsonDocument message = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "event")
            {
                Assert.AreEqual("invalidated", root.GetProperty("event").GetString());
                invalidatedReceived = true;
                continue;
            }

            int requestSequence = root.GetProperty("request_seq").GetInt32();
            if (requestSequence == cancelSequence)
            {
                AssertResponse(root, cancelSequence, "cancel", success: true);
                cancelReceived = true;
            }
            else if (requestSequence == operationSequence)
            {
                AssertResponse(root, operationSequence, command, success: false);
                Assert.Contains(
                    "cancelled",
                    root.GetProperty("message").GetString()!,
                    StringComparison.OrdinalIgnoreCase);
                evaluationReceived = true;
            }
        }
    }

    private async Task ReadFunctionEvaluationStopAsync(
        DapTestClient client,
        int configurationSequence,
        int launchSequence)
    {
        bool configurationReceived = false;
        bool launchReceived = false;
        bool stopped = false;
        while (!configurationReceived || !launchReceived || !stopped)
        {
            using JsonDocument message = await client
                .ReadMessageAsync(TestContext.CancellationToken)
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

            if (root.GetProperty("event").GetString() == "stopped")
            {
                Assert.AreEqual(
                    "breakpoint",
                    root.GetProperty("body").GetProperty("reason").GetString());
                stopped = true;
            }
        }
    }
}
