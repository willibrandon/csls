using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes generation-bound thread and stack resources.
/// </summary>
internal sealed partial class CslsMcpDebuggerResources
{
    /// <summary>
    /// Reads managed threads for an exact stopped generation.
    /// </summary>
    [McpServerResource(
        UriTemplate = "csls://debug/threads/{debugSession}/{stopGeneration}",
        Name = "csls debugger threads",
        MimeType = "application/json")]
    [Description("Managed threads for one explicit debugger stopped generation.")]
    public Task<TextResourceContents> GetThreadsAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        string debugSession,
        string stopGeneration,
        CancellationToken cancellationToken) =>
        ReadAsync(
            requestContext.Params.Uri,
            async () => JsonSerializer.Serialize(
                await _broker.GetThreadsAsync(
                    debugSession,
                    Parse(stopGeneration, 0, nameof(stopGeneration)),
                    cancellationToken).ConfigureAwait(false),
                McpJsonSerializerContext.Default.McpDebugThreadsResult));

    /// <summary>
    /// Reads a bounded managed stack page for an exact stopped generation.
    /// </summary>
    [McpServerResource(
        UriTemplate = "csls://debug/stack/{debugSession}/{stopGeneration}/{threadId}{?startFrame,levels}",
        Name = "csls debugger stack",
        MimeType = "application/json")]
    [Description("Bounded managed stack page for one thread and stopped generation.")]
    public Task<TextResourceContents> GetStackAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        string debugSession,
        string stopGeneration,
        string threadId,
        CancellationToken cancellationToken,
        string? startFrame = null,
        string? levels = null) =>
        ReadAsync(
            requestContext.Params.Uri,
            async () => JsonSerializer.Serialize(
                await _broker.GetStackAsync(
                    debugSession,
                    Parse(stopGeneration, 0, nameof(stopGeneration)),
                    ParseInt(threadId, 0, nameof(threadId)),
                    ParseInt(startFrame, 0, nameof(startFrame)),
                    ParseInt(levels, 0, nameof(levels)),
                    cancellationToken).ConfigureAwait(false),
                McpJsonSerializerContext.Default.McpDebugStackResult));
}
