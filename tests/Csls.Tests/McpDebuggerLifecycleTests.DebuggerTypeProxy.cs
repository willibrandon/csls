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
            [
                "ArrayValue",
                "BoxedValue",
                "ComputedValue",
                "[0]",
                "[1]",
                "ProtectedValue",
                "[0]",
                "[1]",
                "ThrowingValue",
                "Value",
                "_attributedProperty",
                "_attributedValue",
                "Raw View"
            ],
            presented.GetProperty("variables").EnumerateArray()
                .Select(item => item.GetProperty("name").GetString())
                .ToArray());
        JsonElement[] proxyMembers =
        [
            .. presented.GetProperty("variables").EnumerateArray()
        ];
        Assert.AreEqual("{int[2]}", proxyMembers[0].GetProperty("value").GetString());
        Assert.AreEqual("55", proxyMembers[1].GetProperty("value").GetString());
        Assert.AreEqual("52", proxyMembers[2].GetProperty("value").GetString());
        Assert.AreEqual("43", proxyMembers[3].GetProperty("value").GetString());
        Assert.AreEqual("44", proxyMembers[4].GetProperty("value").GetString());
        Assert.AreEqual("45", proxyMembers[5].GetProperty("value").GetString());
        Assert.AreEqual("48", proxyMembers[6].GetProperty("value").GetString());
        Assert.AreEqual("49", proxyMembers[7].GetProperty("value").GetString());
        Assert.StartsWith(
            "<error: System.InvalidOperationException:",
            proxyMembers[8].GetProperty("value").GetString());
        Assert.AreEqual("42", proxyMembers[9].GetProperty("value").GetString());
        Assert.AreEqual("47", proxyMembers[10].GetProperty("value").GetString());
        Assert.AreEqual("46", proxyMembers[11].GetProperty("value").GetString());
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
