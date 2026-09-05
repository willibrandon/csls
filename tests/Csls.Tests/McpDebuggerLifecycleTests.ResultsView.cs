using ModelContextProtocol;
using ModelContextProtocol.Client;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies lazy enumeration and explicit MCP authority over a real debugger session.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    private static async Task<(JsonElement State, string ResourceUri)> AssertAuthorizedResultsViewAsync(
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
        JsonElement localScope = scopes.GetProperty("scopes").EnumerateArray().Single(
            item => item.GetProperty("name").GetString() == "Locals");
        JsonElement locals = await CallAsync(
            client,
            "debug_variables_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["variablesReference"] = localScope.GetProperty("variablesReference").GetInt32()
            },
            cancellationToken).ConfigureAwait(false);
        JsonElement enumerable = locals.GetProperty("variables").EnumerateArray().Single(
            item => item.GetProperty("name").GetString() == "localResultsView");
        var parentArguments = new Dictionary<string, object?>
        {
            ["debugSession"] = debugSession,
            ["stopGeneration"] = generation,
            ["variablesReference"] = enumerable.GetProperty("variablesReference").GetInt32()
        };
        JsonElement fields = await CallAsync(
            client, "debug_variables_get", parentArguments, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(generation, fields.GetProperty("stopGeneration").GetInt64());
        JsonElement resultsView = fields.GetProperty("variables").EnumerateArray().Single(
            item => item.GetProperty("name").GetString() == "Results View");
        Assert.AreEqual("resultsView", resultsView.GetProperty("presentationKind").GetString());
        Assert.AreEqual(
            "Expanding the Results View will enumerate the IEnumerable",
            resultsView.GetProperty("value").GetString());
        var viewArguments = new Dictionary<string, object?>
        {
            ["debugSession"] = debugSession,
            ["stopGeneration"] = generation,
            ["variablesReference"] = resultsView.GetProperty("variablesReference").GetInt32()
        };
        string resourceUri =
            $"csls://debug/variables/{debugSession}/{generation}/{viewArguments["variablesReference"]}";
        await AssertToolErrorAsync(
            client, "debug_variables_get", viewArguments,
            "debugger_operation_failed", cancellationToken).ConfigureAwait(false);
        await AssertToolErrorAsync(
            client, "debug_variables_get_presented", viewArguments,
            "debugger_control_denied", cancellationToken).ConfigureAwait(false);
        await AssertResultsViewResourceReadDeniedAsync(client, resourceUri, cancellationToken)
            .ConfigureAwait(false);
        JsonElement unenumerated = await CallAsync(
            client, "debug_variables_get", parentArguments, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("0", unenumerated.GetProperty("variables").EnumerateArray().Single(
            item => item.GetProperty("name").GetString() == "_enumerationCount")
            .GetProperty("value").GetString());
        Assert.AreEqual(generation, unenumerated.GetProperty("stopGeneration").GetInt64());

        _ = await GrantAgentControlAsync(
            client, debugSession, durationSeconds: 60, cancellationToken).ConfigureAwait(false);
        // Read-only observation remains non-executing even after the separate control grant.
        await AssertToolErrorAsync(
            client, "debug_variables_get", viewArguments,
            "debugger_operation_failed", cancellationToken).ConfigureAwait(false);
        await AssertResultsViewResourceReadDeniedAsync(client, resourceUri, cancellationToken)
            .ConfigureAwait(false);
        JsonElement afterResourceRead = await CallAsync(
            client, "debug_variables_get", parentArguments, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("0", afterResourceRead.GetProperty("variables").EnumerateArray().Single(
            item => item.GetProperty("name").GetString() == "_enumerationCount")
            .GetProperty("value").GetString());
        Assert.AreEqual(generation, afterResourceRead.GetProperty("stopGeneration").GetInt64());
        JsonElement presented = await AssertResourceSubscriptionAsync(
            client,
            resourceUri,
            () => CallAsync(client, "debug_variables_get_presented", viewArguments, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        Assert.IsGreaterThan(generation, presented.GetProperty("stopGeneration").GetInt64());
        long snapshotGeneration = presented.GetProperty("stopGeneration").GetInt64();
        JsonElement snapshot = Assert.ContainsSingle(presented.GetProperty("variables").EnumerateArray());
        Assert.AreEqual("Results View", snapshot.GetProperty("name").GetString());
        Assert.AreEqual("resultsSnapshot", snapshot.GetProperty("presentationKind").GetString());
        Assert.AreEqual(3, snapshot.GetProperty("indexedVariables").GetInt32());
        Assert.AreEqual(0, snapshot.GetProperty("namedVariables").GetInt32());
        int snapshotReference = snapshot.GetProperty("variablesReference").GetInt32();
        Assert.IsGreaterThan(0, snapshotReference);
        Assert.AreNotEqual(resultsView.GetProperty("variablesReference").GetInt32(), snapshotReference);
        await AssertToolErrorAsync(
            client, "debug_variables_get_presented", viewArguments,
            "debugger_stale_generation", cancellationToken).ConfigureAwait(false);
        JsonElement revoked = await CallAsync(
            client,
            "debug_agent_control_set",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["enabled"] = false
            },
            cancellationToken).ConfigureAwait(false);
        Assert.IsFalse(revoked.GetProperty("agentControl").GetBoolean());
        Assert.AreEqual(snapshotGeneration, revoked.GetProperty("stopGeneration").GetInt64());
        var frameArguments = new Dictionary<string, object?>
        {
            ["debugSession"] = debugSession,
            ["stopGeneration"] = generation,
            ["frameId"] = frame.GetProperty("id").GetInt32()
        };
        await AssertToolErrorAsync(
            client, "debug_scopes_get", frameArguments,
            "debugger_stale_generation", cancellationToken).ConfigureAwait(false);
        frameArguments["stopGeneration"] = snapshotGeneration;
        JsonElement refreshedScopes = await CallAsync(
            client, "debug_scopes_get", frameArguments, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(snapshotGeneration, refreshedScopes.GetProperty("stopGeneration").GetInt64());
        JsonElement[] refreshedScopeItems = [.. refreshedScopes.GetProperty("scopes").EnumerateArray()];
        Assert.AreSequenceEqual(
            ["Arguments", "Locals"],
            refreshedScopeItems.Select(scope => scope.GetProperty("name").GetString()).ToArray());
        JsonElement refreshedLocalScope = Assert.ContainsSingle(refreshedScopeItems.Where(scope =>
            scope.GetProperty("name").GetString() == "Locals"));
        int refreshedLocalsReference = refreshedLocalScope.GetProperty("variablesReference").GetInt32();
        Assert.IsGreaterThan(0, refreshedLocalsReference);
        Assert.AreNotEqual(localScope.GetProperty("variablesReference").GetInt32(), refreshedLocalsReference);
        JsonElement refreshedLocals = await CallAsync(
            client,
            "debug_variables_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = snapshotGeneration,
                ["variablesReference"] = refreshedLocalsReference
            },
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(snapshotGeneration, refreshedLocals.GetProperty("stopGeneration").GetInt64());
        JsonElement number = Assert.ContainsSingle(refreshedLocals.GetProperty("variables").EnumerateArray()
            .Where(local => local.GetProperty("name").GetString() == "localNumber"));
        Assert.AreEqual("43", number.GetProperty("value").GetString());
        Assert.AreEqual("int", number.GetProperty("type").GetString());
        Assert.AreEqual("localNumber", number.GetProperty("evaluateName").GetString());
        Assert.AreEqual(0, number.GetProperty("variablesReference").GetInt32());
        await AssertReadonlyResultsViewSnapshotAsync(
            client, debugSession, snapshotGeneration, snapshotReference, cancellationToken)
            .ConfigureAwait(false);
        JsonElement current = await CallAsync(
            client, "debug_session_get",
            new Dictionary<string, object?> { ["debugSession"] = debugSession },
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("stopped", current.GetProperty("state").GetString());
        Assert.AreEqual(
            presented.GetProperty("stopGeneration").GetInt64(),
            current.GetProperty("stopGeneration").GetInt64());
        JsonElement currentFrame = await GetSourceFrameAsync(
            client, debugSession, current.GetProperty("stopGeneration").GetInt64(),
            current.GetProperty("stoppedThreadId").GetInt32(), sourcePath, cancellationToken)
            .ConfigureAwait(false);
        JsonElement counter = await CallAsync(
            client, "debug_evaluate",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = current.GetProperty("stopGeneration").GetInt64(),
                ["frameId"] = currentFrame.GetProperty("id").GetInt32(),
                ["expression"] = "localResultsView._enumerationCount"
            },
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("1", counter.GetProperty("evaluation").GetProperty("result").GetString());
        JsonElement renewed = await GrantAgentControlAsync(
            client, debugSession, durationSeconds: 60, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(snapshotGeneration, renewed.GetProperty("stopGeneration").GetInt64());
        return (renewed, resourceUri);
    }

    private static async Task AssertReadonlyResultsViewSnapshotAsync(
        McpClient client,
        string debugSession,
        long generation,
        int snapshotReference,
        CancellationToken cancellationToken)
    {
        (int Start, int Count, string[] Names, string[] Values)[] pages =
        [
            (0, 1, ["[0]"], ["71"]),
            (1, 1, ["[1]"], ["72"]),
            (2, 4, ["[2]"], ["73"]),
            (3, 1, [], []),
            (0, 0, ["[0]", "[1]", "[2]"], ["71", "72", "73"])
        ];
        foreach ((int start, int count, string[] names, string[] values) in pages)
        {
            var arguments = new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["variablesReference"] = snapshotReference,
                ["start"] = start,
                ["count"] = count
            };
            JsonElement tool = await CallAsync(
                client, "debug_variables_get", arguments, cancellationToken).ConfigureAwait(false);
            AssertResultsViewSnapshotPage(tool, generation, names, values);
            string snapshotUri =
                $"csls://debug/variables/{debugSession}/{generation}/{snapshotReference}?start={start}&count={count}";
            JsonElement resource = await ReadAsync(client, snapshotUri, cancellationToken)
                .ConfigureAwait(false);
            AssertResultsViewSnapshotPage(resource, generation, names, values);
        }
    }

    private static void AssertResultsViewSnapshotPage(
        JsonElement page,
        long generation,
        string[] expectedNames,
        string[] expectedValues)
    {
        Assert.AreEqual(generation, page.GetProperty("stopGeneration").GetInt64());
        JsonElement[] variables = [.. page.GetProperty("variables").EnumerateArray()];
        Assert.AreSequenceEqual(
            expectedNames, variables.Select(item => item.GetProperty("name").GetString()).ToArray());
        Assert.AreSequenceEqual(
            expectedValues, variables.Select(item => item.GetProperty("value").GetString()).ToArray());
        foreach (JsonElement variable in variables)
        {
            Assert.AreEqual("int", variable.GetProperty("type").GetString());
            Assert.AreEqual(0, variable.GetProperty("variablesReference").GetInt32());
            Assert.IsFalse(variable.TryGetProperty("evaluateName", out JsonElement evaluateName) &&
                evaluateName.ValueKind != JsonValueKind.Null);
        }
    }

    private static void AssertExpectedResultsViewDiagnostics(string diagnostics, string resourceUri)
    {
        const string expectedCategory = "fail: ModelContextProtocol.Server.McpServer[";
        string expectedResourceMessage =
            $"      ReadResource \"{resourceUri}\" threw an unhandled exception.";
        const string expectedExceptionMessage =
            "      ModelContextProtocol.McpException: debugger_operation_failed: " +
            "Expanding Results View executes target code and requires explicit target-code authorization.";
        string[] lines = diagnostics.ReplaceLineEndings("\n").Split('\n');
        int expectedDenialCount = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.StartsWith("fail:", StringComparison.OrdinalIgnoreCase))
            {
                Assert.StartsWith(expectedCategory, line, StringComparison.Ordinal);
                Assert.EndsWith("]", line, StringComparison.Ordinal);
                Assert.IsGreaterThan(2, lines.Length - index);
                Assert.AreEqual(expectedResourceMessage, lines[++index]);
                Assert.AreEqual(expectedExceptionMessage, lines[++index]);
                expectedDenialCount++;
                continue;
            }

            Assert.DoesNotContain("fail:", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("crit:", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Unhandled exception", line, StringComparison.OrdinalIgnoreCase);
        }

        Assert.AreEqual(2, expectedDenialCount);
    }

    private static async Task AssertResultsViewResourceReadDeniedAsync(
        McpClient client,
        string resourceUri,
        CancellationToken cancellationToken)
    {
        McpProtocolException exception = await Assert.ThrowsExactlyAsync<McpProtocolException>(
            async () => await client.ReadResourceAsync(
                new Uri(resourceUri),
                cancellationToken: cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.Contains("debugger_operation_failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("target-code authorization", exception.Message, StringComparison.Ordinal);
    }
}
