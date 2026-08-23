using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using LspRange = Csls.Protocol.Range;

namespace Csls.Workspaces;

/// <summary>
/// Maps Razor source positions through SDK-generated C# for project-aware hover.
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
        if (cache.TryGetHover(path, position, out Hover? result))
        {
            return result;
        }

        SourceText razorText = await razorDocument
            .GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        if (cache.Documents.TryGetValue(path, out SourceGeneratedDocument? cachedDocument))
        {
            Hover? cachedHover = await GetGeneratedDocumentHoverAsync(
                cachedDocument,
                path,
                razorText,
                position,
                cancellationToken).ConfigureAwait(false);
            if (cachedHover is not null)
            {
                cache.TryAddHover(path, position, cachedHover);
                return cachedHover;
            }
        }

        IEnumerable<SourceGeneratedDocument> documents = await project
            .GetSourceGeneratedDocumentsAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (SourceGeneratedDocument document in documents.Where(
            document => document.Id != cachedDocument?.Id))
        {
            Hover? hover = await GetMappedDocumentHoverAsync(
                document,
                cache,
                path,
                razorText,
                position,
                cancellationToken).ConfigureAwait(false);
            if (hover is not null)
            {
                cache.TryAddHover(path, position, hover);
                return hover;
            }
        }

        return null;
    }

    private static async Task<Hover?> GetMappedDocumentHoverAsync(
        SourceGeneratedDocument document,
        RazorHoverProjectCache cache,
        string path,
        SourceText razorText,
        Position position,
        CancellationToken cancellationToken)
    {
        if (!await MapsPathAsync(
            document,
            path,
            cache.Documents,
            cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await GetGeneratedDocumentHoverAsync(
            document,
            path,
            razorText,
            position,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Hover?> GetGeneratedDocumentHoverAsync(
        SourceGeneratedDocument document,
        string path,
        SourceText razorText,
        Position position,
        CancellationToken cancellationToken)
    {
        SyntaxNode root = await document
            .GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Roslyn returned no syntax root for generated document {document.Name}.");
        SyntaxTree syntaxTree = root.SyntaxTree;
        SourceText generatedText = await document
            .GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        if (TryMapToGeneratedOffset(
            syntaxTree,
            generatedText,
            razorText,
            path,
            position,
            cancellationToken,
            out int generatedOffset))
        {
            (string Markdown, TextSpan Span)? hover = await WorkspaceHoverService
                .GetAsync(document, generatedOffset, cancellationToken)
                .ConfigureAwait(false);
            if (hover is not null && TryMapRange(
                syntaxTree,
                razorText,
                path,
                hover.Value.Span,
                cancellationToken,
                out LspRange range))
            {
                return new Hover
                {
                    Contents = new MarkupContent
                    {
                        Kind = "markdown",
                        Value = hover.Value.Markdown
                    },
                    Range = range
                };
            }
        }

        return null;
    }

    private static async Task<bool> MapsPathAsync(
        SourceGeneratedDocument document,
        string path,
        ConcurrentDictionary<string, SourceGeneratedDocument> cache,
        CancellationToken cancellationToken)
    {
        SyntaxNode root = await document
            .GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Roslyn returned no syntax root for generated document {document.Name}.");
        bool mapsPath = false;
        foreach (LineMapping mapping in root.SyntaxTree.GetLineMappings(cancellationToken))
        {
            mapsPath |= CacheMapping(mapping, document, path, cache);
        }

        return mapsPath;
    }

    private static bool CacheMapping(
        LineMapping mapping,
        SourceGeneratedDocument document,
        string path,
        ConcurrentDictionary<string, SourceGeneratedDocument> cache)
    {
        string mappedPath = mapping.MappedSpan.Path;
        if (mapping.IsHidden || string.IsNullOrWhiteSpace(mappedPath))
        {
            return false;
        }

        cache.TryAdd(mappedPath, document);
        return PathsEqual(mappedPath, path);
    }

    private static bool TryMapToGeneratedOffset(
        SyntaxTree syntaxTree,
        SourceText generatedText,
        SourceText razorText,
        string path,
        Position position,
        CancellationToken cancellationToken,
        out int generatedOffset)
    {
        generatedOffset = 0;
        var requestedPosition = new LinePosition(position.Line, position.Character);
        if (!TryGetOffset(razorText, requestedPosition, out int requestedOffset))
        {
            return false;
        }

        IEnumerable<(LineMapping Mapping, bool IsEnhanced, int Length)> candidates = syntaxTree
            .GetLineMappings(cancellationToken)
            .Select(mapping => GetMappingCandidate(mapping, razorText, path, requestedOffset))
            .Where(candidate => candidate.HasValue)
            .Select(candidate => candidate.GetValueOrDefault());
        (LineMapping Mapping, bool IsEnhanced, int Length)? best = candidates.Aggregate(
            default((LineMapping Mapping, bool IsEnhanced, int Length)?),
            static (current, candidate) => IsBetterMapping(candidate, current)
                ? candidate
                : current);

        if (best is null)
        {
            return false;
        }

        LineMapping bestMapping = best.Value.Mapping;
        LinePosition mappedStart = bestMapping.MappedSpan.StartLinePosition;
        int lineDelta = requestedPosition.Line - mappedStart.Line;
        int generatedCharacter = bestMapping.CharacterOffset is int characterOffset && lineDelta == 0
            ? characterOffset + requestedPosition.Character - mappedStart.Character
            : requestedPosition.Character;
        var generatedPosition = new LinePosition(
            bestMapping.Span.Start.Line + lineDelta,
            generatedCharacter);
        return Contains(bestMapping.Span, generatedPosition) &&
            TryGetOffset(generatedText, generatedPosition, out generatedOffset);
    }

    private static (LineMapping Mapping, bool IsEnhanced, int Length)? GetMappingCandidate(
        LineMapping mapping,
        SourceText razorText,
        string path,
        int requestedOffset)
    {
        if (mapping.IsHidden ||
            !PathsEqual(mapping.MappedSpan.Path, path) ||
            !TryGetTextSpan(razorText, mapping.MappedSpan.Span, out TextSpan mappedTextSpan) ||
            !mappedTextSpan.Contains(requestedOffset))
        {
            return null;
        }

        return (mapping, mapping.CharacterOffset.HasValue, mappedTextSpan.Length);
    }

    private static bool IsBetterMapping(
        (LineMapping Mapping, bool IsEnhanced, int Length) candidate,
        (LineMapping Mapping, bool IsEnhanced, int Length)? best)
    {
        if (best is null)
        {
            return true;
        }

        if (candidate.IsEnhanced != best.Value.IsEnhanced)
        {
            return candidate.IsEnhanced;
        }

        return candidate.Length < best.Value.Length;
    }

    private static bool TryMapRange(
        SyntaxTree syntaxTree,
        SourceText razorText,
        string path,
        TextSpan generatedSpan,
        CancellationToken cancellationToken,
        out LspRange range)
    {
        FileLinePositionSpan mappedSpan = syntaxTree.GetMappedLineSpan(
            generatedSpan,
            cancellationToken);
        if (mappedSpan.IsValid &&
            PathsEqual(mappedSpan.Path, path) &&
            TryGetTextSpan(razorText, mappedSpan.Span, out _))
        {
            range = new LspRange(
                new Position(
                    mappedSpan.StartLinePosition.Line,
                    mappedSpan.StartLinePosition.Character),
                new Position(
                    mappedSpan.EndLinePosition.Line,
                    mappedSpan.EndLinePosition.Character));
            return true;
        }

        range = default;
        return false;
    }

    private static bool TryGetTextSpan(
        SourceText text,
        LinePositionSpan lineSpan,
        out TextSpan textSpan)
    {
        if (TryGetOffset(text, lineSpan.Start, out int start) &&
            TryGetOffset(text, lineSpan.End, out int end) &&
            start <= end)
        {
            textSpan = TextSpan.FromBounds(start, end);
            return true;
        }

        textSpan = default;
        return false;
    }

    private static bool TryGetOffset(
        SourceText text,
        LinePosition position,
        out int offset)
    {
        if (position.Line >= 0 && position.Line < text.Lines.Count)
        {
            TextLine line = text.Lines[position.Line];
            if (position.Character >= 0 && position.Character <= line.Span.Length)
            {
                offset = line.Start + position.Character;
                return true;
            }
        }

        offset = 0;
        return false;
    }

    private static bool Contains(LinePositionSpan span, LinePosition position) =>
        position.CompareTo(span.Start) >= 0 && position.CompareTo(span.End) < 0;

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            left,
            right,
            PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
