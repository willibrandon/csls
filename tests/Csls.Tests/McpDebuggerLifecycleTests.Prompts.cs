using ModelContextProtocol.Protocol;
using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Verifies debugger prompts through a real MCP worker process.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    /// <summary>
    /// Grounds dump triage in one explicit read-only session and supported evidence.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DebuggerDumpTriagePromptUsesExplicitReadOnlyEvidence()
    {
        McpProcessSession mcp = await StartMcpAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
        GetPromptResult result = await mcp.Client.GetPromptAsync(
            "triage_dotnet_dump",
            new Dictionary<string, object?>
            {
                ["debugSession"] = "dump-session-42",
                ["question"] = "Why did request processing stop?"
            },
            cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);

        PromptMessage message = Assert.ContainsSingle(result.Messages);
        Assert.AreEqual(Role.User, message.Role);
        Assert.IsInstanceOfType<TextContentBlock>(message.Content);
        string text = ((TextContentBlock)message.Content).Text;
        Assert.Contains("dump-session-42", text, StringComparison.Ordinal);
        Assert.Contains("Why did request processing stop?", text, StringComparison.Ordinal);
        Assert.Contains("debug_session_get", text, StringComparison.Ordinal);
        Assert.Contains("stopGeneration", text, StringComparison.Ordinal);
        Assert.Contains("debug_threads_get", text, StringComparison.Ordinal);
        Assert.Contains("debug_stack_get", text, StringComparison.Ordinal);
        Assert.Contains("debug_modules_get", text, StringComparison.Ordinal);
        Assert.Contains("read-only", text, StringComparison.Ordinal);
        Assert.Contains("do not grant control", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unavailable locals", text, StringComparison.Ordinal);
    }
}
