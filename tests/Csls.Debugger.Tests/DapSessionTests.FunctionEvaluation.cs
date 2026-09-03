using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies explicitly authorized target-code evaluation through real DAP processes.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Invokes a parameterless instance method and restores the stopped target afterward.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedFunctionEvaluationInvokesAndCancelsInstanceMethods()
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
            JsonElement unsupportedArgument = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.LengthForDebugger(\"answer!\")",
                success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains(
                "string arguments",
                unsupportedArgument.GetProperty("message").GetString()!,
                StringComparison.OrdinalIgnoreCase);
            JsonElement afterUnsupportedArgument = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.Number",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "42",
                afterUnsupportedArgument.GetProperty("result").GetString());

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

            frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement afterException = await ReadEvaluationAsync(
                client,
                frame.GetProperty("id").GetInt32(),
                "localObject.Number",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("42", afterException.GetProperty("result").GetString());

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
            int cancelSequence = await client.SendRequestAsync(
                "cancel",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("requestId", cancelableEvaluationSequence);
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertCanceledFunctionEvaluationAsync(
                client,
                cancelableEvaluationSequence,
                cancelSequence).ConfigureAwait(false);

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

    private async Task AssertCanceledFunctionEvaluationAsync(
        DapTestClient client,
        int evaluationSequence,
        int cancelSequence)
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
            else if (requestSequence == evaluationSequence)
            {
                AssertResponse(root, evaluationSequence, "evaluate", success: false);
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
