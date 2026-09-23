using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes managed module and exception resources.
/// </summary>
internal sealed partial class CslsMcpDebuggerResources
{
    /// <summary>
    /// Reads a bounded managed-module page.
    /// </summary>
    [McpServerResource(
        UriTemplate = "csls://debug/modules/{debugSession}{?startModule,moduleCount}",
        Name = "csls debugger modules",
        MimeType = "application/json")]
    [Description("Bounded managed modules and validated symbol status for one debugger session.")]
    public Task<TextResourceContents> GetModulesAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        string debugSession,
        CancellationToken cancellationToken,
        string? startModule = null,
        string? moduleCount = null) =>
        ReadAsync(
            requestContext.Params.Uri,
            async () => JsonSerializer.Serialize(
                await _broker.GetModulesAsync(
                    debugSession,
                    ParseInt(startModule, 0, nameof(startModule)),
                    ParseInt(moduleCount, 0, nameof(moduleCount)),
                    cancellationToken).ConfigureAwait(false),
                McpJsonSerializerContext.Default.McpDebugModulesResult));

    /// <summary>
    /// Reads the managed exception for an exact stopped generation.
    /// </summary>
    [McpServerResource(
        UriTemplate = "csls://debug/exception/{debugSession}/{stopGeneration}/{threadId}",
        Name = "csls debugger exception",
        MimeType = "application/json")]
    [Description("Managed exception detail for one thread and stopped generation.")]
    public Task<TextResourceContents> GetExceptionAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        string debugSession,
        string stopGeneration,
        string threadId,
        CancellationToken cancellationToken) =>
        ReadAsync(
            requestContext.Params.Uri,
            async () => JsonSerializer.Serialize(
                await _broker.GetExceptionAsync(
                    debugSession,
                    Parse(stopGeneration, 0, nameof(stopGeneration)),
                    ParseInt(threadId, 0, nameof(threadId)),
                    cancellationToken).ConfigureAwait(false),
                McpJsonSerializerContext.Default.McpDebugExceptionResult));
}
