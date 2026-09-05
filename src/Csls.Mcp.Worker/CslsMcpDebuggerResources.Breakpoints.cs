using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes authoritative debugger breakpoint state as an MCP resource.
/// </summary>
internal sealed partial class CslsMcpDebuggerResources
{
    /// <summary>
    /// Reads every source, function, instruction, and exception breakpoint.
    /// </summary>
    [McpServerResource(
        UriTemplate = "csls://debug/breakpoints/{debugSession}",
        Name = "csls debugger breakpoints",
        MimeType = "application/json")]
    [Description("Every authoritative breakpoint and exception policy for one debugger session.")]
    public Task<TextResourceContents> GetBreakpointsAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        string debugSession,
        CancellationToken cancellationToken) =>
        ReadAsync(
            requestContext.Params.Uri,
            async () => JsonSerializer.Serialize(
                await _broker.GetBreakpointsAsync(debugSession, cancellationToken)
                    .ConfigureAwait(false),
                McpJsonSerializerContext.Default.McpDebugBreakpointsResult));
}
