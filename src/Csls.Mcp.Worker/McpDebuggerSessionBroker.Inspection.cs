using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Reads bounded generation-aware debugger state.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker
{
    private const int MaximumPageSize = 256;

    /// <summary>
    /// Gets managed threads for the selected stop generation.
    /// </summary>
    internal Task<McpDebugThreadsResult> GetThreadsAsync(
        string debugSession,
        long stopGeneration,
        CancellationToken cancellationToken) =>
        InvokeStoppedAsync(
            debugSession,
            stopGeneration,
            async (session, client, token) => new McpDebugThreadsResult(
                session.Id,
                stopGeneration,
                await client.GetThreadsAsync(token).ConfigureAwait(false)),
            cancellationToken);

    /// <summary>
    /// Gets a bounded managed stack page for the selected stop generation.
    /// </summary>
    internal Task<McpDebugStackResult> GetStackAsync(
        string debugSession,
        long stopGeneration,
        int threadId,
        int startFrame,
        int levels,
        CancellationToken cancellationToken)
    {
        ValidatePositive(threadId, nameof(threadId));
        ValidatePage(startFrame, levels, nameof(startFrame), nameof(levels));
        return InvokeStoppedAsync(
            debugSession,
            stopGeneration,
            async (session, client, token) =>
            {
                DebugStackTrace stack = await client.GetStackAsync(
                    new DebugStackRequest(threadId, startFrame, levels),
                    token).ConfigureAwait(false);
                return new McpDebugStackResult(
                    session.Id,
                    stopGeneration,
                    stack.StackFrames,
                    stack.TotalFrames);
            },
            cancellationToken);
    }

    /// <summary>
    /// Gets scopes for one current-generation frame.
    /// </summary>
    internal Task<McpDebugScopesResult> GetScopesAsync(
        string debugSession,
        long stopGeneration,
        int frameId,
        CancellationToken cancellationToken)
    {
        ValidatePositive(frameId, nameof(frameId));
        return InvokeStoppedAsync(
            debugSession,
            stopGeneration,
            async (session, client, token) => new McpDebugScopesResult(
                session.Id,
                stopGeneration,
                await client.GetScopesAsync(new DebugScopesRequest(frameId), token)
                    .ConfigureAwait(false)),
            cancellationToken);
    }

    /// <summary>
    /// Gets a bounded page from one current-generation variable container.
    /// </summary>
    internal Task<McpDebugVariablesResult> GetVariablesAsync(
        string debugSession,
        long stopGeneration,
        int variablesReference,
        int start,
        int count,
        CancellationToken cancellationToken)
    {
        ValidatePositive(variablesReference, nameof(variablesReference));
        ValidatePage(start, count, nameof(start), nameof(count));
        return InvokeStoppedAsync(
            debugSession,
            stopGeneration,
            async (session, client, token) => new McpDebugVariablesResult(
                session.Id,
                stopGeneration,
                await client.GetVariablesAsync(
                    new DebugVariablesRequest(variablesReference, start, count),
                    token).ConfigureAwait(false)),
            cancellationToken);
    }

    /// <summary>
    /// Evaluates a side-effect-free expression in a current-generation frame.
    /// </summary>
    internal Task<McpDebugEvaluationResult> EvaluateAsync(
        string debugSession,
        long stopGeneration,
        int frameId,
        string expression,
        CancellationToken cancellationToken)
    {
        ValidatePositive(frameId, nameof(frameId));
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw InvalidRequest("expression must not be empty.");
        }

        return InvokeStoppedAsync(
            debugSession,
            stopGeneration,
            async (session, client, token) => new McpDebugEvaluationResult(
                session.Id,
                stopGeneration,
                await client.EvaluateAsync(
                    new DebugEvaluateRequest(frameId, expression),
                    token).ConfigureAwait(false)),
            cancellationToken);
    }

    /// <summary>
    /// Gets a bounded managed module page for the selected session.
    /// </summary>
    internal async Task<McpDebugModulesResult> GetModulesAsync(
        string debugSession,
        int startModule,
        int moduleCount,
        CancellationToken cancellationToken)
    {
        ValidatePage(startModule, moduleCount, nameof(startModule), nameof(moduleCount));
        McpDebuggerSession session = Resolve(debugSession);
        DebugModulePage page = await session.InvokeAsync(
            (client, token) => client.GetModulesAsync(
                new DebugModulesRequest(startModule, moduleCount),
                token),
            cancellationToken).ConfigureAwait(false);
        return new McpDebugModulesResult(
            session.Id,
            page.Modules.Select(McpDebugModuleInfo.Create).ToArray(),
            page.TotalModules);
    }

}
