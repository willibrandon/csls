using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using LspRange = Csls.Protocol.Range;

namespace Csls.Workspaces;

/// <summary>
/// Maps current Razor source positions through SDK-generated C# line directives.
/// </summary>
internal static class WorkspaceRazorMappingService
{
    private static readonly ConditionalWeakTable<
        Project,
        RazorMappingProjectCache> s_projectCache = [];

    /// <summary>
    /// Resolves a Razor source position within its owning immutable solution snapshot.
    /// </summary>
    /// <param name="solution">The immutable workspace solution snapshot.</param>
    /// <param name="path">The absolute Razor document path.</param>
    /// <param name="position">The zero-based UTF-16 Razor position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The mapped generated document and offset, or null when no mapping contains the position.</returns>
    internal static async Task<RazorMappedDocument?> ResolveAsync(
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
                RazorMappedDocument? mappedDocument = await ResolveProjectAsync(
                    project,
                    razorDocument,
                    path,
                    position,
                    cancellationToken).ConfigureAwait(false);
                if (mappedDocument is not null)
                {
                    return mappedDocument;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a Razor source position within one owning immutable project snapshot.
    /// </summary>
    /// <param name="project">The owning Roslyn project.</param>
    /// <param name="razorDocument">The current Razor additional document.</param>
    /// <param name="path">The absolute Razor document path.</param>
    /// <param name="position">The zero-based UTF-16 Razor position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The mapped generated document and offset, or null when no mapping contains the position.</returns>
    internal static async Task<RazorMappedDocument?> ResolveProjectAsync(
        Project project,
        TextDocument razorDocument,
        string path,
        Position position,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(razorDocument);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RazorMappingProjectCache cache = s_projectCache.GetValue(
            project,
            static _ => new RazorMappingProjectCache());
        SourceText razorText = await razorDocument
            .GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        if (cache.Documents.TryGetValue(path, out SourceGeneratedDocument? cachedDocument))
        {
            RazorMappedDocument? cachedMapping = await MapDocumentAsync(
                cachedDocument,
                path,
                razorText,
                position,
                cancellationToken).ConfigureAwait(false);
            if (cachedMapping is not null)
            {
                return cachedMapping;
            }
        }

        IEnumerable<SourceGeneratedDocument> generatedDocuments = await project
            .GetSourceGeneratedDocumentsAsync(cancellationToken)
            .ConfigureAwait(false);
        SourceGeneratedDocument[] documents =
        [
            .. generatedDocuments.Where(document => document.Id != cachedDocument?.Id)
        ];
        for (int index = 0; index < documents.Length; index++)
        {
            RazorMappedDocument? mapping = await MapUncachedDocumentAsync(
                documents[index],
                cache,
                path,
                razorText,
                position,
                cancellationToken).ConfigureAwait(false);
            if (mapping is not null)
            {
                return mapping;
            }
        }

        return null;
    }

    /// <summary>
    /// Maps a generated C# source span back to its current Razor source range.
    /// </summary>
    /// <param name="mappedDocument">The generated document context.</param>
    /// <param name="generatedSpan">The generated C# source span.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <param name="range">The mapped Razor range when successful.</param>
    /// <returns>True when the span maps to the same current Razor document.</returns>
    internal static bool TryMapRange(
        RazorMappedDocument mappedDocument,
        TextSpan generatedSpan,
        CancellationToken cancellationToken,
        out LspRange range)
    {
        ArgumentNullException.ThrowIfNull(mappedDocument);
        FileLinePositionSpan mappedSpan = mappedDocument.SyntaxTree.GetMappedLineSpan(
            generatedSpan,
            cancellationToken);
        if (mappedSpan.IsValid &&
            PathsEqual(mappedSpan.Path, mappedDocument.RazorPath) &&
            TryGetTextSpan(mappedDocument.RazorText, mappedSpan.Span, out _))
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

    private static async Task<RazorMappedDocument?> MapUncachedDocumentAsync(
        SourceGeneratedDocument document,
        RazorMappingProjectCache cache,
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

        return await MapDocumentAsync(
            document,
            path,
            razorText,
            position,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RazorMappedDocument?> MapDocumentAsync(
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
        return TryMapToGeneratedOffset(
            syntaxTree,
            generatedText,
            razorText,
            path,
            position,
            cancellationToken,
            out int generatedOffset)
            ? new RazorMappedDocument(
                document,
                syntaxTree,
                razorText,
                path,
                generatedOffset)
            : null;
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

    /// <summary>
    /// Converts a valid UTF-16 line span to an absolute text span.
    /// </summary>
    /// <param name="text">The immutable source text.</param>
    /// <param name="lineSpan">The line span to convert.</param>
    /// <param name="textSpan">The converted text span when successful.</param>
    /// <returns>True when both line positions are valid and ordered.</returns>
    internal static bool TryGetTextSpan(
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
        string.Equals(left, right, PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
