using Csls.Debugger.Contracts;
using StreamJsonRpc;

namespace Csls.Mcp.Worker;

/// <summary>
/// Reads bounded generation-aware debugger state.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker
{
    private const int MaximumPageSize = 256;
    private const int MaximumWatchCount = 64;
    private const int MaximumExpressionLength = 4096;

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
                    new DebugVariablesRequest(
                        variablesReference,
                        start,
                        count,
                        AllowTargetCodeExecution: false),
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
        ValidateExpression(expression);

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
    /// Evaluates a bounded ordered watch set without letting one invalid expression hide the rest.
    /// </summary>
    /// <param name="debugSession">The exact debugger-session identifier.</param>
    /// <param name="stopGeneration">The exact current stopped generation.</param>
    /// <param name="frameId">The generation-bound managed frame handle.</param>
    /// <param name="expressions">The ordered side-effect-free expressions.</param>
    /// <param name="cancellationToken">Cancels evaluation.</param>
    /// <returns>The ordered current-generation watch results.</returns>
    internal Task<McpDebugWatchesResult> GetWatchesAsync(
        string debugSession,
        long stopGeneration,
        int frameId,
        IReadOnlyList<string> expressions,
        CancellationToken cancellationToken)
    {
        ValidatePositive(frameId, nameof(frameId));
        ArgumentNullException.ThrowIfNull(expressions);
        if (expressions.Count is 0 or > MaximumWatchCount)
        {
            throw InvalidRequest(
                $"expressions must contain between one and {MaximumWatchCount} items.");
        }

        foreach (string expression in expressions)
        {
            ValidateExpression(expression);
        }

        return InvokeStoppedAsync(
            debugSession,
            stopGeneration,
            async (session, client, token) =>
            {
                List<McpDebugWatchValue> watches = new(expressions.Count);
                foreach (string expression in expressions)
                {
                    try
                    {
                        DebugEvaluateResult evaluation = await client.EvaluateAsync(
                            new DebugEvaluateRequest(frameId, expression),
                            token).ConfigureAwait(false);
                        watches.Add(new McpDebugWatchValue(expression, evaluation, Error: null));
                    }
                    catch (RemoteInvocationException exception)
                    {
                        watches.Add(new McpDebugWatchValue(
                            expression,
                            Evaluation: null,
                            new McpDebuggerError(
                                "debugger_evaluation_failed",
                                exception.Message)));
                    }
                }

                return new McpDebugWatchesResult(session.Id, stopGeneration, watches);
            },
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
