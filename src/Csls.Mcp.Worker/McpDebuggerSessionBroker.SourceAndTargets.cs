using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Reads debugger source and runtime-approved navigation targets for MCP agents.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker
{
    private const int MaximumSourceCharacters = 65_536;

    /// <summary>
    /// Gets one bounded source-text page at an exact stop.
    /// </summary>
    internal Task<McpDebugSourceResult> GetSourceAsync(
        string debugSession,
        long stopGeneration,
        int sourceReference,
        int start,
        int count,
        CancellationToken cancellationToken)
    {
        ValidatePositive(sourceReference, nameof(sourceReference));
        if (start < 0 || count is <= 0 or > MaximumSourceCharacters)
        {
            throw InvalidRequest(
                $"start must be non-negative and count must be between 1 and " +
                $"{MaximumSourceCharacters} characters.");
        }

        return InvokeStoppedAsync(
            debugSession,
            stopGeneration,
            async (session, client, token) =>
            {
                DebugSourceContent source = await client.GetSourceContentAsync(
                    new DebugSourceRequest(sourceReference),
                    token).ConfigureAwait(false);
                if (start > source.Content.Length)
                {
                    throw InvalidRequest(
                        $"start {start} exceeds the source length {source.Content.Length}.");
                }

                int length = Math.Min(count, source.Content.Length - start);
                int nextStart = start + length;
                return new McpDebugSourceResult(
                    session.Id,
                    stopGeneration,
                    source.Content.Substring(start, length),
                    source.MimeType,
                    start,
                    source.Content.Length,
                    nextStart < source.Content.Length ? nextStart : null);
            },
            cancellationToken);
    }

    /// <summary>
    /// Gets source-aware Step Into targets for one frame at an exact stop.
    /// </summary>
    internal Task<McpDebugStepTargetsResult> GetStepTargetsAsync(
        string debugSession,
        long stopGeneration,
        int frameId,
        CancellationToken cancellationToken)
    {
        ValidatePositive(frameId, nameof(frameId));
        return InvokeStoppedAsync(
            debugSession,
            stopGeneration,
            async (session, client, token) => new McpDebugStepTargetsResult(
                session.Id,
                stopGeneration,
                await client.GetStepTargetsAsync(
                    new DebugStepTargetsRequest(frameId),
                    token).ConfigureAwait(false)),
            cancellationToken);
    }

    /// <summary>
    /// Gets runtime-approved go-to destinations for one source position.
    /// </summary>
    internal Task<McpDebugGotoTargetsResult> GetGotoTargetsAsync(
        string debugSession,
        long stopGeneration,
        DebugGotoTargetsRequest request,
        CancellationToken cancellationToken)
    {
        ValidatePositive(request.FrameId, "frameId");
        ValidateSourcePath(request.SourcePath);
        ValidatePositive(request.Line, "line");
        if (request.Column is <= 0)
        {
            throw InvalidRequest("column must be positive when specified.");
        }

        return InvokeStoppedAsync(
            debugSession,
            stopGeneration,
            async (session, client, token) => new McpDebugGotoTargetsResult(
                session.Id,
                stopGeneration,
                await client.GetGotoTargetsAsync(request, token).ConfigureAwait(false)),
            cancellationToken);
    }
}
