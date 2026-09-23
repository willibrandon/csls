using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies cursor-addressable MCP target output from a real debuggee.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    private static async Task AssertOutputAsync(
        McpClient client,
        string debugSession,
        long generation,
        CancellationToken cancellationToken)
    {
        JsonElement output;
        while (true)
        {
            output = await CallAsync(
                client,
                "debug_output_get",
                new Dictionary<string, object?>
                {
                    ["debugSession"] = debugSession,
                    ["count"] = 1
                },
                cancellationToken).ConfigureAwait(false);
            if (output.GetProperty("entries").GetArrayLength() != 0)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken)
                .ConfigureAwait(false);
        }

        Assert.AreEqual("stopped", output.GetProperty("state").GetString());
        Assert.AreEqual(generation, output.GetProperty("stopGeneration").GetInt64());
        Assert.AreEqual(0, output.GetProperty("droppedBeforeStart").GetInt64());
        JsonElement entry = output.GetProperty("entries")[0];
        Assert.AreEqual("ready", entry.GetProperty("output").GetString());
        Assert.AreEqual("standardOutput", entry.GetProperty("category").GetString());
        Assert.IsFalse(entry.GetProperty("truncated").GetBoolean());
        long cursor = output.GetProperty("nextSequence").GetInt64();
        Assert.IsGreaterThan(0, cursor);

        JsonElement next = await CallAsync(
            client,
            "debug_output_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["afterSequence"] = cursor,
                ["count"] = 1
            },
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, next.GetProperty("entries").GetArrayLength());
        Assert.AreEqual(cursor, next.GetProperty("nextSequence").GetInt64());
        Assert.IsFalse(next.GetProperty("hasMore").GetBoolean());

        ReadResourceResult sessionResource = await client.ReadResourceAsync(
            new Uri($"csls://debug/session/{debugSession}"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement resourceSession = ParseResource(sessionResource);
        Assert.AreEqual(debugSession, resourceSession.GetProperty("debugSession").GetString());
        Assert.AreEqual(generation, resourceSession.GetProperty("stopGeneration").GetInt64());

        ReadResourceResult outputResource = await client.ReadResourceAsync(
            new Uri($"csls://debug/output/{debugSession}?afterSequence=0&count=1"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement resourceOutput = ParseResource(outputResource);
        Assert.AreEqual("ready", resourceOutput.GetProperty("entries")[0]
            .GetProperty("output").GetString());
    }

    private static JsonElement ParseResource(ReadResourceResult result)
    {
        TextResourceContents content = Assert.ContainsSingle(
            result.Contents.OfType<TextResourceContents>());
        using var document = JsonDocument.Parse(content.Text);
        return document.RootElement.Clone();
    }
}
