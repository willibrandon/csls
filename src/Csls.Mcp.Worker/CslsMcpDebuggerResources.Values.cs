using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes generation-bound scope and variable resources.
/// </summary>
internal sealed partial class CslsMcpDebuggerResources
{
    /// <summary>
    /// Reads scopes for one generation-bound frame.
    /// </summary>
    [McpServerResource(
        UriTemplate = "csls://debug/scopes/{debugSession}/{stopGeneration}/{frameId}",
        Name = "csls debugger scopes",
        MimeType = "application/json")]
    [Description("Managed scopes for one frame and stopped generation.")]
    public Task<TextResourceContents> GetScopesAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        string debugSession,
        string stopGeneration,
        string frameId,
        CancellationToken cancellationToken) =>
        ReadAsync(
            requestContext.Params.Uri,
            async () => JsonSerializer.Serialize(
                await _broker.GetScopesAsync(
                    debugSession,
                    Parse(stopGeneration, 0, nameof(stopGeneration)),
                    ParseInt(frameId, 0, nameof(frameId)),
                    cancellationToken).ConfigureAwait(false),
                McpJsonSerializerContext.Default.McpDebugScopesResult));

    /// <summary>
    /// Reads a bounded page from one generation-bound variable container.
    /// </summary>
    [McpServerResource(
        UriTemplate = "csls://debug/variables/{debugSession}/{stopGeneration}/{variablesReference}{?start,count}",
        Name = "csls debugger variables",
        MimeType = "application/json")]
    [Description("Bounded variables for one container and stopped generation.")]
    public Task<TextResourceContents> GetVariablesAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        string debugSession,
        string stopGeneration,
        string variablesReference,
        CancellationToken cancellationToken,
        string? start = null,
        string? count = null) =>
        ReadAsync(
            requestContext.Params.Uri,
            async () => JsonSerializer.Serialize(
                await _broker.GetVariablesAsync(
                    debugSession,
                    Parse(stopGeneration, 0, nameof(stopGeneration)),
                    ParseInt(variablesReference, 0, nameof(variablesReference)),
                    ParseInt(start, 0, nameof(start)),
                    ParseInt(count, 0, nameof(count)),
                    cancellationToken).ConfigureAwait(false),
                McpJsonSerializerContext.Default.McpDebugVariablesResult));

    /// <summary>
    /// Reads one side-effect-free watch expression for a generation-bound frame.
    /// </summary>
    [McpServerResource(
        UriTemplate = "csls://debug/watches/{debugSession}/{stopGeneration}/{frameId}{?expression}",
        Name = "csls debugger watch",
        MimeType = "application/json")]
    [Description("One independently evaluated watch for a frame and stopped generation.")]
    public Task<TextResourceContents> GetWatchAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        string debugSession,
        string stopGeneration,
        string frameId,
        string expression,
        CancellationToken cancellationToken) =>
        ReadAsync(
            requestContext.Params.Uri,
            async () => JsonSerializer.Serialize(
                await _broker.GetWatchesAsync(
                    debugSession,
                    Parse(stopGeneration, 0, nameof(stopGeneration)),
                    ParseInt(frameId, 0, nameof(frameId)),
                    [expression],
                    cancellationToken).ConfigureAwait(false),
                McpJsonSerializerContext.Default.McpDebugWatchesResult));
}
