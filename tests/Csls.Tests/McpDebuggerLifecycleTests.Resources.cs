using ModelContextProtocol.Client;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies MCP debugger inspection resources against a real stopped target.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    private static async Task AssertInspectionResourcesAsync(
        McpClient client,
        string debugSession,
        long generation,
        int threadId,
        int frameId,
        int variablesReference,
        string sourcePath,
        string modulePath,
        CancellationToken cancellationToken)
    {
        JsonElement threads = await ReadAsync(
            client,
            $"csls://debug/threads/{debugSession}/{generation}",
            cancellationToken).ConfigureAwait(false);
        Assert.Contains(
            threadId,
            threads.GetProperty("threads").EnumerateArray()
                .Select(static thread => thread.GetProperty("id").GetInt32()));

        JsonElement stack = await ReadAsync(
            client,
            $"csls://debug/stack/{debugSession}/{generation}/{threadId}?levels=64",
            cancellationToken).ConfigureAwait(false);
        Assert.Contains(
            sourcePath,
            stack.GetProperty("stackFrames").EnumerateArray()
                .Where(static frame => frame.TryGetProperty("source", out _))
                .Select(static frame => frame.GetProperty("source")
                    .GetProperty("path").GetString()),
            StringComparer.Ordinal);

        JsonElement scopes = await ReadAsync(
            client,
            $"csls://debug/scopes/{debugSession}/{generation}/{frameId}",
            cancellationToken).ConfigureAwait(false);
        Assert.Contains(
            "Locals",
            scopes.GetProperty("scopes").EnumerateArray()
                .Select(static scope => scope.GetProperty("name").GetString()));

        JsonElement variables = await ReadAsync(
            client,
            $"csls://debug/variables/{debugSession}/{generation}/{variablesReference}?count=20",
            cancellationToken).ConfigureAwait(false);
        Assert.Contains(
            "localNumber",
            variables.GetProperty("variables").EnumerateArray()
                .Select(static variable => variable.GetProperty("name").GetString()));

        JsonElement modules = await ReadAsync(
            client,
            $"csls://debug/modules/{debugSession}",
            cancellationToken).ConfigureAwait(false);
        Assert.Contains(
            modulePath,
            modules.GetProperty("modules").EnumerateArray()
                .Select(static module => module.GetProperty("path").GetString()),
            StringComparer.Ordinal);
    }

    private static async Task<JsonElement> ReadAsync(
        McpClient client,
        string uri,
        CancellationToken cancellationToken) =>
        ParseResource(await client.ReadResourceAsync(
            new Uri(uri),
            cancellationToken: cancellationToken).ConfigureAwait(false));
}
