using Csls.Debugger.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes generation-bound source, memory, and managed-IL resources.
/// </summary>
internal sealed partial class CslsMcpDebuggerResources
{
    private const string DisassemblyTemplate =
        "csls://debug/disassembly/{debugSession}/{stopGeneration}" +
        "{?instructionReference,byteOffset,instructionOffset,instructionCount,resolveSymbols}";

    /// <summary>
    /// Reads a bounded source page for one source reference.
    /// </summary>
    [McpServerResource(
        UriTemplate = "csls://debug/source/{debugSession}/{stopGeneration}/{sourceReference}{?start,count}",
        Name = "csls debugger source",
        MimeType = "application/json")]
    [Description("Bounded source text for one reference and stopped generation.")]
    public Task<TextResourceContents> GetSourceAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        string debugSession,
        string stopGeneration,
        string sourceReference,
        CancellationToken cancellationToken,
        string? start = null,
        string? count = null) =>
        ReadAsync(
            requestContext.Params.Uri,
            async () => JsonSerializer.Serialize(
                await _broker.GetSourceAsync(
                    debugSession,
                    Parse(stopGeneration, 0, nameof(stopGeneration)),
                    ParseInt(sourceReference, 0, nameof(sourceReference)),
                    ParseInt(start, 0, nameof(start)),
                    ParseInt(count, 16_384, nameof(count)),
                    cancellationToken).ConfigureAwait(false),
                McpJsonSerializerContext.Default.McpDebugSourceResult));

    /// <summary>
    /// Reads bounded target memory through an opaque stopped-state reference.
    /// </summary>
    [McpServerResource(
        UriTemplate = "csls://debug/memory/{debugSession}/{stopGeneration}{?memoryReference,offset,count}",
        Name = "csls debugger memory",
        MimeType = "application/json")]
    [Description("Bounded target memory for one opaque reference and stopped generation.")]
    public Task<TextResourceContents> ReadMemoryAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        string debugSession,
        string stopGeneration,
        string memoryReference,
        string count,
        CancellationToken cancellationToken,
        string? offset = null) =>
        ReadAsync(
            requestContext.Params.Uri,
            async () => JsonSerializer.Serialize(
                await _broker.ReadMemoryAsync(
                    debugSession,
                    Parse(stopGeneration, 0, nameof(stopGeneration)),
                    memoryReference,
                    ParseSigned(offset, 0, nameof(offset)),
                    ParseInt(count, 0, nameof(count)),
                    cancellationToken).ConfigureAwait(false),
                McpJsonSerializerContext.Default.McpDebugMemoryResult));

    /// <summary>
    /// Reads a bounded exact-count managed-IL instruction range.
    /// </summary>
    [McpServerResource(
        UriTemplate = DisassemblyTemplate,
        Name = "csls debugger disassembly",
        MimeType = "application/json")]
    [Description("Bounded managed-IL instructions for one reference and stopped generation.")]
    public Task<TextResourceContents> DisassembleAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        string debugSession,
        string stopGeneration,
        string instructionReference,
        string instructionCount,
        CancellationToken cancellationToken,
        string? byteOffset = null,
        string? instructionOffset = null,
        string? resolveSymbols = null) =>
        ReadAsync(
            requestContext.Params.Uri,
            async () => JsonSerializer.Serialize(
                await _broker.DisassembleAsync(
                    debugSession,
                    Parse(stopGeneration, 0, nameof(stopGeneration)),
                    new DebugDisassemblyRequest(
                        instructionReference,
                        ParseSigned(byteOffset, 0, nameof(byteOffset)),
                        ParseSigned(instructionOffset, 0, nameof(instructionOffset)),
                        ParseInt(instructionCount, 0, nameof(instructionCount)),
                        ParseBoolean(resolveSymbols, true, nameof(resolveSymbols))),
                    cancellationToken).ConfigureAwait(false),
                McpJsonSerializerContext.Default.McpDebugDisassemblyResult));
}
