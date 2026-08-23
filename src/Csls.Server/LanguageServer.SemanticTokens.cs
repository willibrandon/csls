using Csls.Core;
using Csls.Protocol;

namespace Csls.Server;

/// <summary>
/// Implements generation-safe complete and delta semantic-token requests.
/// </summary>
public sealed partial class LanguageServer
{
    private readonly SemanticTokensCache _semanticTokensCache = new();

    /// <inheritdoc />
    public Task<SemanticTokens> SemanticTokensFullAsync(
        SemanticTokensParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<int> data = await _workspaceManager
                    .GetSemanticTokensAsync(parameters, context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while semantic tokens were being computed.");
                }

                return _semanticTokensCache.StoreFull(
                    parameters.TextDocument.Uri,
                    context.WorkspaceGeneration,
                    data);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<SemanticTokensDeltaResult> SemanticTokensFullDeltaAsync(
        SemanticTokensDeltaParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                IReadOnlyList<int> data = await _workspaceManager
                    .GetSemanticTokensAsync(
                        new SemanticTokensParams
                        {
                            TextDocument = parameters.TextDocument
                        },
                        context.CancellationToken)
                    .ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        "The workspace changed while semantic-token delta was being computed.");
                }

                return _semanticTokensCache.StoreDelta(
                    parameters.TextDocument.Uri,
                    context.WorkspaceGeneration,
                    parameters.PreviousResultId,
                    data);
            },
            cancellationToken);
    }
}
