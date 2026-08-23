using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using LspRange = Csls.Protocol.Range;

namespace Csls.Workspaces;

/// <summary>
/// Resolves project-aware hover through SDK-generated Razor C# documents.
/// </summary>
internal static class WorkspaceRazorHoverService
{
    private static readonly ConditionalWeakTable<
        Project,
        RazorHoverProjectCache> s_projectCache = [];

    /// <summary>
    /// Resolves hover from the generated documents owned by one Razor additional document.
    /// </summary>
    /// <param name="solution">The immutable workspace solution snapshot.</param>
    /// <param name="path">The absolute Razor document path.</param>
    /// <param name="position">The zero-based UTF-16 Razor position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>Mapped Razor hover, or null when the position has no generated C# symbol.</returns>
    internal static async Task<Hover?> GetHoverAsync(
        Solution solution,
        string path,
        Position position,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ImmutableArray<DocumentId> documentIds = solution.GetDocumentIdsWithFilePath(path);
        for (int index = 0; index < documentIds.Length; index++)
        {
            DocumentId documentId = documentIds[index];
            TextDocument? razorDocument = solution.GetAdditionalDocument(documentId);
            Project? project = solution.GetProject(documentId.ProjectId);
            if (razorDocument is not null && project is not null)
            {
                Hover? hover = await GetProjectHoverAsync(
                    project,
                    razorDocument,
                    path,
                    position,
                    cancellationToken).ConfigureAwait(false);
                if (hover is not null)
                {
                    return hover;
                }
            }
        }

        return null;
    }

    private static async Task<Hover?> GetProjectHoverAsync(
        Project project,
        TextDocument razorDocument,
        string path,
        Position position,
        CancellationToken cancellationToken)
    {
        RazorHoverProjectCache cache = s_projectCache.GetValue(
            project,
            static _ => new RazorHoverProjectCache());
        if (cache.TryGetHover(path, position, out Hover? cachedHover))
        {
            return cachedHover;
        }

        RazorMappedDocument? mappedDocument = await WorkspaceRazorMappingService
            .ResolveProjectAsync(
                project,
                razorDocument,
                path,
                position,
                cancellationToken)
            .ConfigureAwait(false);
        if (mappedDocument is null)
        {
            return null;
        }

        (string Markdown, TextSpan Span)? hover = await WorkspaceHoverService
            .GetAsync(
                mappedDocument.Document,
                mappedDocument.GeneratedOffset,
                cancellationToken)
            .ConfigureAwait(false);
        if (hover is null ||
            !WorkspaceRazorMappingService.TryMapRange(
                mappedDocument,
                hover.Value.Span,
                cancellationToken,
                out LspRange range))
        {
            return null;
        }

        var result = new Hover
        {
            Contents = new MarkupContent
            {
                Kind = "markdown",
                Value = hover.Value.Markdown
            },
            Range = range
        };
        cache.TryAddHover(path, position, result);
        return result;
    }
}
