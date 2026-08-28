using Csls.Protocol;
using Microsoft.CodeAnalysis;

namespace Csls.Workspaces;

/// <summary>
/// Exposes generation-safe hierarchy and inlay-hint operations for workspace snapshots.
/// </summary>
public sealed partial class WorkspaceManager
{
    /// <summary>
    /// Prepares the callable declaration at one source position.
    /// </summary>
    /// <param name="parameters">The target document position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The prepared call-hierarchy item, or an empty list.</returns>
    public Task<IReadOnlyList<CallHierarchyItem>> PrepareCallHierarchyAsync(
        CallHierarchyPrepareParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return WorkspaceHierarchyService.PrepareCallHierarchyAsync(
            FindCurrentDocument(parameters.TextDocument.Uri),
            parameters.Position,
            Generation,
            cancellationToken);
    }

    /// <summary>
    /// Finds direct source callers for one prepared callable item.
    /// </summary>
    /// <param name="parameters">The prepared callable item.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded direct incoming calls.</returns>
    public Task<IReadOnlyList<CallHierarchyIncomingCall>> GetIncomingCallsAsync(
        CallHierarchyIncomingCallsParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        (Document document, HierarchyItemData data) = ResolveHierarchyItem(
            parameters.Item.Uri,
            parameters.Item.Data);
        return WorkspaceHierarchyService.GetIncomingCallsAsync(
            document,
            data.Position,
            data.Generation,
            cancellationToken);
    }

    /// <summary>
    /// Finds direct source callees for one prepared callable item.
    /// </summary>
    /// <param name="parameters">The prepared callable item.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded direct outgoing calls.</returns>
    public Task<IReadOnlyList<CallHierarchyOutgoingCall>> GetOutgoingCallsAsync(
        CallHierarchyOutgoingCallsParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        (Document document, HierarchyItemData data) = ResolveHierarchyItem(
            parameters.Item.Uri,
            parameters.Item.Data);
        return WorkspaceHierarchyService.GetOutgoingCallsAsync(
            document,
            data.Position,
            data.Generation,
            cancellationToken);
    }

    /// <summary>
    /// Prepares the named type declaration at one source position.
    /// </summary>
    /// <param name="parameters">The target document position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The prepared type-hierarchy item, or an empty list.</returns>
    public Task<IReadOnlyList<TypeHierarchyItem>> PrepareTypeHierarchyAsync(
        TypeHierarchyPrepareParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return WorkspaceHierarchyService.PrepareTypeHierarchyAsync(
            FindCurrentDocument(parameters.TextDocument.Uri),
            parameters.Position,
            Generation,
            cancellationToken);
    }

    /// <summary>
    /// Finds direct source supertypes for one prepared type item.
    /// </summary>
    /// <param name="parameters">The prepared type item.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded direct source supertypes.</returns>
    public Task<IReadOnlyList<TypeHierarchyItem>> GetSupertypesAsync(
        TypeHierarchySupertypesParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        (Document document, HierarchyItemData data) = ResolveHierarchyItem(
            parameters.Item.Uri,
            parameters.Item.Data);
        return WorkspaceHierarchyService.GetSupertypesAsync(
            document,
            data.Position,
            data.Generation,
            cancellationToken);
    }

    /// <summary>
    /// Finds direct source subtypes for one prepared type item.
    /// </summary>
    /// <param name="parameters">The prepared type item.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded direct source subtypes.</returns>
    public Task<IReadOnlyList<TypeHierarchyItem>> GetSubtypesAsync(
        TypeHierarchySubtypesParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        (Document document, HierarchyItemData data) = ResolveHierarchyItem(
            parameters.Item.Uri,
            parameters.Item.Data);
        return WorkspaceHierarchyService.GetSubtypesAsync(
            document,
            data.Position,
            data.Generation,
            cancellationToken);
    }

    /// <summary>
    /// Gets semantic inlay hints for one visible source range.
    /// </summary>
    /// <param name="parameters">The target document and visible range.</param>
    /// <param name="includeParameterHints">Whether parameter-name hints are enabled.</param>
    /// <param name="includeTypeHints">Whether inferred-type hints are enabled.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded ordered inlay hints.</returns>
    public Task<IReadOnlyList<InlayHint>> GetInlayHintsAsync(
        InlayHintParams parameters,
        bool includeParameterHints,
        bool includeTypeHints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return WorkspaceInlayHintService.GetInlayHintsAsync(
            FindCurrentDocument(parameters.TextDocument.Uri),
            parameters.Range,
            Generation,
            includeParameterHints,
            includeTypeHints,
            cancellationToken);
    }

    /// <summary>
    /// Resolves semantic details for one server-produced inlay hint.
    /// </summary>
    /// <param name="hint">The hint to resolve.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The hint populated with semantic tooltip information.</returns>
    public Task<InlayHint> ResolveInlayHintAsync(
        InlayHint hint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hint);
        InlayHintData data = hint.Data
            ?? throw new InvalidOperationException("The inlay hint has no resolve data.");
        if (data.Generation != Generation)
        {
            throw new InvalidOperationException(
                "The workspace changed after the inlay hint was produced.");
        }

        Document document = FindCurrentDocument(data.Uri)
            ?? throw new InvalidOperationException(
                $"Inlay-hint document {data.Uri} is unavailable.");
        return WorkspaceInlayHintService.ResolveAsync(document, hint, cancellationToken);
    }

    private (Document Document, HierarchyItemData Data) ResolveHierarchyItem(
        DocumentUri itemUri,
        HierarchyItemData? data)
    {
        if (data is null)
        {
            throw new InvalidOperationException("The hierarchy item has no expansion data.");
        }

        if (data.Generation != Generation)
        {
            throw new InvalidOperationException(
                "The workspace changed after the hierarchy item was produced.");
        }

        if (data.Uri != itemUri)
        {
            throw new InvalidOperationException(
                "The hierarchy item's source URI does not match its expansion data.");
        }

        Document document = FindCurrentDocument(data.Uri)
            ?? throw new InvalidOperationException(
                $"Hierarchy document {data.Uri} is unavailable.");
        return (document, data);
    }
}
