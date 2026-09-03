using ModelContextProtocol.Client;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies explicitly authorized MCP target-code evaluation.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    private static async Task<JsonElement> AssertAuthorizedExpressionExecutionAsync(
        McpClient client,
        JsonElement stopped,
        string sourcePath,
        string cancellationSignalPath,
        CancellationToken cancellationToken)
    {
        string debugSession = stopped.GetProperty("debugSession").GetString()!;
        long generation = stopped.GetProperty("stopGeneration").GetInt64();
        int threadId = stopped.GetProperty("stoppedThreadId").GetInt32();
        JsonElement frame = await GetSourceFrameAsync(
            client,
            debugSession,
            generation,
            threadId,
            sourcePath,
            cancellationToken).ConfigureAwait(false);
        JsonElement primitiveResult = await CallAsync(
            client,
            "debug_execute_expression",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["frameId"] = frame.GetProperty("id").GetInt32(),
                ["expression"] = "localObject.AddForDebugger(localNumber - 42)"
            },
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            "43",
            primitiveResult.GetProperty("evaluation").GetProperty("result").GetString());
        Assert.IsTrue(
            primitiveResult
                .GetProperty("evaluation")
                .GetProperty("targetCodeExecuted")
                .GetBoolean());
        long primitiveGeneration = primitiveResult.GetProperty("stopGeneration").GetInt64();
        Assert.IsGreaterThan(generation, primitiveGeneration);

        frame = await GetSourceFrameAsync(
            client,
            debugSession,
            primitiveGeneration,
            threadId,
            sourcePath,
            cancellationToken).ConfigureAwait(false);
        JsonElement stringResult = await CallAsync(
            client,
            "debug_execute_expression",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = primitiveGeneration,
                ["frameId"] = frame.GetProperty("id").GetInt32(),
                ["expression"] = "localObject.LengthForDebugger(\"answer!\")"
            },
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            "7",
            stringResult.GetProperty("evaluation").GetProperty("result").GetString());
        Assert.IsGreaterThan(
            primitiveGeneration,
            stringResult.GetProperty("stopGeneration").GetInt64());
        long stringGeneration = stringResult.GetProperty("stopGeneration").GetInt64();

        frame = await GetSourceFrameAsync(
            client,
            debugSession,
            stringGeneration,
            threadId,
            sourcePath,
            cancellationToken).ConfigureAwait(false);
        JsonElement staticResult = await CallAsync(
            client,
            "debug_execute_expression",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = stringGeneration,
                ["frameId"] = frame.GetProperty("id").GetInt32(),
                ["expression"] = "System.Math.Abs(-42)"
            },
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            "42",
            staticResult.GetProperty("evaluation").GetProperty("result").GetString());
        Assert.IsGreaterThan(
            stringGeneration,
            staticResult.GetProperty("stopGeneration").GetInt64());
        long staticGeneration = staticResult.GetProperty("stopGeneration").GetInt64();

        frame = await GetSourceFrameAsync(
            client,
            debugSession,
            staticGeneration,
            threadId,
            sourcePath,
            cancellationToken).ConfigureAwait(false);
        JsonElement assignment = await CallAsync(
            client,
            "debug_expression_set",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = staticGeneration,
                ["frameId"] = frame.GetProperty("id").GetInt32(),
                ["expression"] = "localObject.Number",
                ["value"] = "50"
            },
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(staticGeneration, assignment.GetProperty("stopGeneration").GetInt64());
        Assert.AreEqual(
            "50",
            assignment.GetProperty("variable").GetProperty("value").GetString());
        JsonElement assignedValue = await CallAsync(
            client,
            "debug_evaluate",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = staticGeneration,
                ["frameId"] = frame.GetProperty("id").GetInt32(),
                ["expression"] = "localObject.Number"
            },
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            "50",
            assignedValue.GetProperty("evaluation").GetProperty("result").GetString());
        JsonElement scopes = await CallAsync(
            client,
            "debug_scopes_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = staticGeneration,
                ["frameId"] = frame.GetProperty("id").GetInt32()
            },
            cancellationToken).ConfigureAwait(false);
        JsonElement locals = scopes.GetProperty("scopes").EnumerateArray().Single(item =>
            item.GetProperty("name").GetString() == "Locals");
        JsonElement variableAssignment = await CallAsync(
            client,
            "debug_variable_set",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = staticGeneration,
                ["variablesReference"] = locals.GetProperty("variablesReference").GetInt32(),
                ["name"] = "localNumber",
                ["value"] = "51"
            },
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            "51",
            variableAssignment.GetProperty("variable").GetProperty("value").GetString());

        await AssertToolErrorAsync(
            client,
            "debug_threads_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation
            },
            "debugger_stale_generation",
            cancellationToken).ConfigureAwait(false);
        await AssertToolErrorAsync(
            client,
            "debug_expression_set",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["frameId"] = frame.GetProperty("id").GetInt32(),
                ["expression"] = "localObject.Number",
                ["value"] = "52"
            },
            "debugger_stale_generation",
            cancellationToken).ConfigureAwait(false);
        JsonElement current = await CallAsync(
            client,
            "debug_session_get",
            new Dictionary<string, object?> { ["debugSession"] = debugSession },
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("stopped", current.GetProperty("state").GetString());
        Assert.AreEqual(
            staticGeneration,
            current.GetProperty("stopGeneration").GetInt64());
        return await AssertExpressionCancellationAsync(
            client,
            current,
            sourcePath,
            cancellationSignalPath,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement> AssertExpressionCancellationAsync(
        McpClient client,
        JsonElement stopped,
        string sourcePath,
        string cancellationSignalPath,
        CancellationToken cancellationToken)
    {
        string debugSession = stopped.GetProperty("debugSession").GetString()!;
        long generation = stopped.GetProperty("stopGeneration").GetInt64();
        JsonElement frame = await GetSourceFrameAsync(
            client,
            debugSession,
            generation,
            stopped.GetProperty("stoppedThreadId").GetInt32(),
            sourcePath,
            cancellationToken).ConfigureAwait(false);
        using var evaluationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task<ModelContextProtocol.Protocol.CallToolResult> evaluation = client.CallToolAsync(
            "debug_execute_expression",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["frameId"] = frame.GetProperty("id").GetInt32(),
                ["expression"] = "localObject.WaitForDebuggerCancellation()"
            },
            cancellationToken: evaluationCancellation.Token).AsTask();
        await FileTextWaiter.WaitAsync(
            cancellationSignalPath,
            "started",
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        await evaluationCancellation.CancelAsync().ConfigureAwait(false);
        OperationCanceledException? cancellation = null;
        try
        {
            _ = await evaluation.ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            cancellation = exception;
        }

        Assert.IsNotNull(cancellation);
        JsonElement current = await CallAsync(
            client,
            "debug_session_get",
            new Dictionary<string, object?> { ["debugSession"] = debugSession },
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("stopped", current.GetProperty("state").GetString());
        Assert.IsGreaterThan(
            generation,
            current.GetProperty("stopGeneration").GetInt64());
        return current;
    }

    private static async Task<JsonElement> GetSourceFrameAsync(
        McpClient client,
        string debugSession,
        long generation,
        int threadId,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        JsonElement stack = await CallAsync(
            client,
            "debug_stack_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["threadId"] = threadId,
                ["levels"] = 64
            },
            cancellationToken).ConfigureAwait(false);
        return stack.GetProperty("stackFrames").EnumerateArray().Single(item =>
            item.TryGetProperty("source", out JsonElement source) &&
            source.TryGetProperty("path", out JsonElement path) &&
            string.Equals(path.GetString(), sourcePath, StringComparison.Ordinal));
    }
}
