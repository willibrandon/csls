using Csls.Core;
using Csls.Protocol;

namespace Csls.Server;

/// <summary>
/// Implements generation-safe call hierarchy, type hierarchy, and inlay-hint requests.
/// </summary>
public sealed partial class LanguageServer
{
    /// <inheritdoc />
    public Task<IReadOnlyList<CallHierarchyItem>> PrepareCallHierarchyAsync(
        CallHierarchyPrepareParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return ScheduleWorkspaceReadAsync(
            token => _workspaceManager.PrepareCallHierarchyAsync(parameters, token),
            "call hierarchy preparation",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CallHierarchyIncomingCall>> CallHierarchyIncomingCallsAsync(
        CallHierarchyIncomingCallsParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return ScheduleWorkspaceReadAsync(
            token => _workspaceManager.GetIncomingCallsAsync(parameters, token),
            "incoming call hierarchy",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CallHierarchyOutgoingCall>> CallHierarchyOutgoingCallsAsync(
        CallHierarchyOutgoingCallsParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return ScheduleWorkspaceReadAsync(
            token => _workspaceManager.GetOutgoingCallsAsync(parameters, token),
            "outgoing call hierarchy",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TypeHierarchyItem>> PrepareTypeHierarchyAsync(
        TypeHierarchyPrepareParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return ScheduleWorkspaceReadAsync(
            token => _workspaceManager.PrepareTypeHierarchyAsync(parameters, token),
            "type hierarchy preparation",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TypeHierarchyItem>> TypeHierarchySupertypesAsync(
        TypeHierarchySupertypesParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return ScheduleWorkspaceReadAsync(
            token => _workspaceManager.GetSupertypesAsync(parameters, token),
            "type hierarchy supertypes",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TypeHierarchyItem>> TypeHierarchySubtypesAsync(
        TypeHierarchySubtypesParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return ScheduleWorkspaceReadAsync(
            token => _workspaceManager.GetSubtypesAsync(parameters, token),
            "type hierarchy subtypes",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<InlayHint>> InlayHintAsync(
        InlayHintParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return ScheduleWorkspaceReadAsync(
            token => _workspaceManager.GetInlayHintsAsync(parameters, token),
            "inlay hints",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<InlayHint> InlayHintResolveAsync(
        InlayHint hint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hint);
        return ScheduleWorkspaceReadAsync(
            token => _workspaceManager.ResolveInlayHintAsync(hint, token),
            "inlay hint resolve",
            cancellationToken);
    }

    private Task<T> ScheduleWorkspaceReadAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string feature,
        CancellationToken cancellationToken)
    {
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            RequestMode.ReadOnly,
            () => _workspaceManager.Generation,
            async context =>
            {
                T result = await operation(context.CancellationToken).ConfigureAwait(false);
                if (_workspaceManager.Generation != context.WorkspaceGeneration)
                {
                    throw new InvalidOperationException(
                        $"The workspace changed while {feature} was being computed.");
                }

                return result;
            },
            cancellationToken);
    }
}
