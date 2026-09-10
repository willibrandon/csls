using ModelContextProtocol.Client;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies explicit time-bounded MCP debugger authorization.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    private static async Task AssertAgentControlLifecycleAsync(
        McpClient client,
        JsonElement stopped,
        CancellationToken cancellationToken)
    {
        string debugSession = stopped.GetProperty("debugSession").GetString()!;
        await AssertToolErrorAsync(
            client,
            "debug_agent_control_set",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["enabled"] = true
            },
            "debugger_request_invalid",
            cancellationToken).ConfigureAwait(false);
        await AssertToolErrorAsync(
            client,
            "debug_agent_control_set",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["enabled"] = true,
                ["durationSeconds"] = 3601
            },
            "debugger_request_invalid",
            cancellationToken).ConfigureAwait(false);
        await AssertToolErrorAsync(
            client,
            "debug_agent_control_set",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["enabled"] = false,
                ["durationSeconds"] = 1
            },
            "debugger_request_invalid",
            cancellationToken).ConfigureAwait(false);
        string sessionResource = $"csls://debug/session/{debugSession}";
        JsonElement granted = await AssertResourceSubscriptionAsync(
            client,
            sessionResource,
            () => GrantAgentControlAsync(
                client,
                debugSession,
                durationSeconds: 1,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(granted.GetProperty("agentControl").GetBoolean());
        DateTimeOffset expiresAt = granted.GetProperty("agentControlExpiresAtUtc")
            .GetDateTimeOffset();
        Assert.IsGreaterThan(DateTimeOffset.UtcNow, expiresAt);

        JsonElement expired = await AssertResourceSubscriptionAsync(
            client,
            sessionResource,
            () => WaitForAgentControlExpiryAsync(
                client,
                debugSession,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

        Assert.IsFalse(expired.TryGetProperty("agentControlExpiresAtUtc", out _));
        await AssertToolErrorAsync(
            client,
            "debug_execution_control",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["operation"] = "continue",
                ["stopGeneration"] = stopped.GetProperty("stopGeneration").GetInt64()
            },
            "debugger_control_denied",
            cancellationToken).ConfigureAwait(false);

        JsonElement renewed = await GrantAgentControlAsync(
            client,
            debugSession,
            durationSeconds: 60,
            cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(renewed.GetProperty("agentControl").GetBoolean());
        JsonElement paused = await CallAsync(
            client,
            "debug_execution_control",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["operation"] = "pause"
            },
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("stopped", paused.GetProperty("state").GetString());
        Assert.AreEqual(renewed.GetProperty("stopGeneration").GetInt64(), paused.GetProperty("stopGeneration").GetInt64());
        Assert.AreEqual(renewed.GetProperty("stopReason").GetString(), paused.GetProperty("stopReason").GetString());
        Assert.AreEqual(renewed.GetProperty("stoppedThreadId").GetInt32(), paused.GetProperty("stoppedThreadId").GetInt32());
        Assert.AreEqual(renewed.GetProperty("processId").GetInt32(), paused.GetProperty("processId").GetInt32());
        JsonElement revoked = await AssertResourceSubscriptionAsync(
            client,
            sessionResource,
            () => CallAsync(
                client,
                "debug_agent_control_set",
                new Dictionary<string, object?>
                {
                    ["debugSession"] = debugSession,
                    ["enabled"] = false
                },
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
        Assert.IsFalse(revoked.GetProperty("agentControl").GetBoolean());
        Assert.IsFalse(revoked.TryGetProperty("agentControlExpiresAtUtc", out _));
        await AssertToolErrorAsync(
            client,
            "debug_execution_control",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["operation"] = "pause"
            },
            "debugger_control_denied",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement> WaitForAgentControlExpiryAsync(
        McpClient client,
        string debugSession,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken)
                .ConfigureAwait(false);
            JsonElement session = await CallAsync(
                client,
                "debug_session_get",
                new Dictionary<string, object?> { ["debugSession"] = debugSession },
                cancellationToken).ConfigureAwait(false);
            if (!session.GetProperty("agentControl").GetBoolean())
            {
                return session;
            }
        }
    }

    private static async Task AssertForeignConnectionCannotGrantAsync(
        string debugSession,
        CancellationToken cancellationToken)
    {
        McpProcessSession other = await StartMcpAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = other.ConfigureAwait(false);
        await AssertToolErrorAsync(
            other.Client,
            "debug_agent_control_set",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["enabled"] = true,
                ["durationSeconds"] = 60
            },
            "debugger_session_not_found",
            cancellationToken).ConfigureAwait(false);
    }

    private static Task<JsonElement> GrantAgentControlAsync(
        McpClient client,
        string debugSession,
        int durationSeconds,
        CancellationToken cancellationToken) =>
        CallAsync(
            client,
            "debug_agent_control_set",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["enabled"] = true,
                ["durationSeconds"] = durationSeconds
            },
            cancellationToken);
}
