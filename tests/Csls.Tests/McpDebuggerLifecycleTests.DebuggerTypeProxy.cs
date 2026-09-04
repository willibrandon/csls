using ModelContextProtocol.Client;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies explicitly authorized debugger presentation over MCP.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    private static async Task<JsonElement> AssertAuthorizedDebuggerTypeProxyAsync(
        McpClient client,
        JsonElement stopped,
        string sourcePath,
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
        JsonElement scopes = await CallAsync(
            client,
            "debug_scopes_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["frameId"] = frame.GetProperty("id").GetInt32()
            },
            cancellationToken).ConfigureAwait(false);
        JsonElement locals = scopes.GetProperty("scopes").EnumerateArray().Single(item =>
            item.GetProperty("name").GetString() == "Locals");
        JsonElement variables = await CallAsync(
            client,
            "debug_variables_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["variablesReference"] = locals.GetProperty("variablesReference").GetInt32()
            },
            cancellationToken).ConfigureAwait(false);
        JsonElement localProxy = variables.GetProperty("variables").EnumerateArray().Single(
            item => item.GetProperty("name").GetString() == "localProxy");
        int proxyReference = localProxy.GetProperty("variablesReference").GetInt32();
        string resourceUri =
            $"csls://debug/variables/{debugSession}/{generation}/{proxyReference}";
        JsonElement presented = await AssertResourceSubscriptionAsync(
            client,
            resourceUri,
            () => CallAsync(
                client,
                "debug_variables_get_presented",
                new Dictionary<string, object?>
                {
                    ["debugSession"] = debugSession,
                    ["stopGeneration"] = generation,
                    ["variablesReference"] = proxyReference
                },
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
        Assert.IsGreaterThan(generation, presented.GetProperty("stopGeneration").GetInt64());
        Assert.AreSequenceEqual(
            ["Value", "[0]", "[1]", "Raw View"],
            presented.GetProperty("variables").EnumerateArray()
                .Select(item => item.GetProperty("name").GetString())
                .ToArray());
        JsonElement current = await CallAsync(
            client,
            "debug_session_get",
            new Dictionary<string, object?> { ["debugSession"] = debugSession },
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            presented.GetProperty("stopGeneration").GetInt64(),
            current.GetProperty("stopGeneration").GetInt64());
        return current;
    }
}
