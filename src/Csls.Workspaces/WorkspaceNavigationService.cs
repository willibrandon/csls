using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using LspDocumentHighlight = Csls.Protocol.DocumentHighlight;
using LspDocumentHighlightKind = Csls.Protocol.DocumentHighlightKind;
using LspLocation = Csls.Protocol.Location;
using LspRange = Csls.Protocol.Range;
using LspSelectionRange = Csls.Protocol.SelectionRange;
using RoslynLocation = Microsoft.CodeAnalysis.Location;

namespace Csls.Workspaces;

/// <summary>
/// Computes bounded semantic navigation results from immutable Roslyn document snapshots.
/// </summary>
internal static class WorkspaceNavigationService
{
    private const int MaximumNavigationLocations = 2_000;
    private const int MaximumSelectionPositions = 1_000;

    /// <summary>
    /// Finds source definitions for a symbol in one immutable document snapshot.
    /// </summary>
    /// <param name="document">The resolved Roslyn document, when present.</param>
    /// <param name="position">The target UTF-16 position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded source definition locations.</returns>
    internal static async Task<IReadOnlyList<LspLocation>> GetDefinitionsAsync(
        Document? document,
        Position position,
        CancellationToken cancellationToken)
    {
        (Document? sourceDocument, ISymbol? symbol) = await FindSymbolAsync(
            document,
            position,
            cancellationToken).ConfigureAwait(false);
        if (sourceDocument is null || symbol is null)
        {
            return [];
        }

        ISymbol definition = await SymbolFinder.FindSourceDefinitionAsync(
            symbol,
            sourceDocument.Project.Solution,
            cancellationToken).ConfigureAwait(false) ?? symbol;
        return await CreateNavigationLocationsAsync(
            sourceDocument.Project,
            definition,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds source declarations for a symbol in one immutable document snapshot.
    /// </summary>
    /// <param name="document">The resolved Roslyn document, when present.</param>
    /// <param name="position">The target UTF-16 position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded source declaration locations.</returns>
    internal static async Task<IReadOnlyList<LspLocation>> GetDeclarationsAsync(
        Document? document,
        Position position,
        CancellationToken cancellationToken)
    {
        (Document? sourceDocument, ISymbol? symbol) = await FindSymbolAsync(
            document,
            position,
            cancellationToken).ConfigureAwait(false);
        if (sourceDocument is null || symbol is null)
        {
            return [];
        }

        IReadOnlyList<LspLocation> declarations = await CreateNavigationLocationsAsync(
            sourceDocument.Project,
            symbol,
            cancellationToken).ConfigureAwait(false);
        if (declarations.Count > 0)
        {
            return declarations;
        }

        ISymbol? sourceSymbol = await SymbolFinder.FindSourceDefinitionAsync(
            symbol,
            sourceDocument.Project.Solution,
            cancellationToken).ConfigureAwait(false);
        return sourceSymbol is null
            ? []
            : await CreateNavigationLocationsAsync(
                sourceDocument.Project,
                sourceSymbol,
                cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds source definitions for a symbol type in one immutable document snapshot.
    /// </summary>
    /// <param name="document">The resolved Roslyn document, when present.</param>
    /// <param name="position">The target UTF-16 position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded source type-definition locations.</returns>
    internal static async Task<IReadOnlyList<LspLocation>> GetTypeDefinitionsAsync(
        Document? document,
        Position position,
        CancellationToken cancellationToken)
    {
        (Document? sourceDocument, ISymbol? symbol) = await FindSymbolAsync(
            document,
            position,
            cancellationToken).ConfigureAwait(false);
        if (sourceDocument is null || symbol is null)
        {
            return [];
        }

        ITypeSymbol? type = GetSymbolType(symbol);
        if (type is null)
        {
            return [];
        }

        ISymbol typeDefinition = type.OriginalDefinition;
        ISymbol sourceDefinition = await SymbolFinder.FindSourceDefinitionAsync(
            typeDefinition,
            sourceDocument.Project.Solution,
            cancellationToken).ConfigureAwait(false) ?? typeDefinition;
        return await CreateNavigationLocationsAsync(
            sourceDocument.Project,
            sourceDefinition,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds source implementations for a symbol in one immutable document snapshot.
    /// </summary>
    /// <param name="document">The resolved Roslyn document, when present.</param>
    /// <param name="position">The target UTF-16 position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded source implementation locations.</returns>
    internal static async Task<IReadOnlyList<LspLocation>> GetImplementationsAsync(
        Document? document,
        Position position,
        CancellationToken cancellationToken)
    {
        (Document? sourceDocument, ISymbol? symbol) = await FindSymbolAsync(
            document,
            position,
            cancellationToken).ConfigureAwait(false);
        if (sourceDocument is null || symbol is null)
        {
            return [];
        }

        IEnumerable<ISymbol> implementations = await SymbolFinder.FindImplementationsAsync(
            symbol,
            sourceDocument.Project.Solution,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var locations = new HashSet<LspLocation>();
        foreach (ISymbol implementation in implementations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddNavigationLocations(
                locations,
                sourceDocument.Project,
                implementation.Locations);
            if (locations.Count >= MaximumNavigationLocations)
            {
                break;
            }
        }

        return OrderNavigationLocations(locations);
    }

    /// <summary>
    /// Gets nested syntax selections for ordered positions in one immutable document snapshot.
    /// </summary>
    /// <param name="document">The resolved Roslyn document, when present.</param>
    /// <param name="positions">The ordered UTF-16 positions.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>One inner-to-outer selection hierarchy per position.</returns>
    internal static async Task<IReadOnlyList<LspSelectionRange>> GetSelectionRangesAsync(
        Document? document,
        IReadOnlyList<Position> positions,
        CancellationToken cancellationToken)
    {
        if (positions.Count > MaximumSelectionPositions)
        {
            throw new ArgumentException(
                $"Selection range requests cannot exceed {MaximumSelectionPositions} positions.",
                nameof(positions));
        }

        if (document is null)
        {
            return [];
        }

        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no syntax root.");
        var ranges = new List<LspSelectionRange>(positions.Count);
        foreach (Position position in positions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int offset = LspPositionConverter.GetOffset(text, position);
            ranges.Add(CreateSelectionRange(root, text, offset));
        }

        return ranges;
    }

    /// <summary>
    /// Gets semantic symbol occurrences within one immutable source document snapshot.
    /// </summary>
    /// <param name="document">The resolved Roslyn document, when present.</param>
    /// <param name="position">The target UTF-16 position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded ordered read, write, and declaration highlights.</returns>
    internal static async Task<IReadOnlyList<LspDocumentHighlight>> GetDocumentHighlightsAsync(
        Document? document,
        Position position,
        CancellationToken cancellationToken)
    {
        (Document? sourceDocument, ISymbol? symbol) = await FindSymbolAsync(
            document,
            position,
            cancellationToken).ConfigureAwait(false);
        if (sourceDocument is null || symbol is null)
        {
            return [];
        }

        var targetUri = DocumentUri.FromFileSystemPath(
            sourceDocument.FilePath
                ?? throw new InvalidOperationException("The Roslyn document has no file path."));
        SemanticModel semanticModel = await sourceDocument
            .GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no semantic model.");
        SyntaxNode root = await sourceDocument
            .GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no syntax root.");
        IEnumerable<ReferencedSymbol> referencedSymbols = await SymbolFinder.FindReferencesAsync(
            symbol,
            sourceDocument.Project.Solution,
            cancellationToken).ConfigureAwait(false);
        var highlights = new Dictionary<LspRange, LspDocumentHighlightKind>();
        foreach (ReferencedSymbol referencedSymbol in referencedSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (RoslynLocation declaration in referencedSymbol.Definition.Locations)
            {
                AddDocumentHighlight(
                    highlights,
                    sourceDocument.Project,
                    targetUri,
                    declaration,
                    LspDocumentHighlightKind.Text);
            }

            foreach (ReferenceLocation reference in referencedSymbol.Locations)
            {
                if (reference.Document.Id != sourceDocument.Id)
                {
                    continue;
                }

                AddDocumentHighlight(
                    highlights,
                    sourceDocument.Project,
                    targetUri,
                    reference.Location,
                    IsWrittenReference(
                        root,
                        semanticModel,
                        reference.Location.SourceSpan,
                        cancellationToken)
                        ? LspDocumentHighlightKind.Write
                        : LspDocumentHighlightKind.Read);
            }

            if (highlights.Count >= MaximumNavigationLocations)
            {
                break;
            }
        }

        return
        [
            .. highlights
                .OrderBy(static highlight => highlight.Key.Start.Line)
                .ThenBy(static highlight => highlight.Key.Start.Character)
                .Take(MaximumNavigationLocations)
                .Select(static highlight => new LspDocumentHighlight
                {
                    Range = highlight.Key,
                    Kind = highlight.Value
                })
        ];
    }

    /// <summary>
    /// Finds source references for a symbol in one immutable document snapshot.
    /// </summary>
    /// <param name="document">The resolved Roslyn document, when present.</param>
    /// <param name="position">The target UTF-16 position.</param>
    /// <param name="includeDeclaration">Whether declaration locations are included.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded deduplicated source reference locations.</returns>
    internal static async Task<IReadOnlyList<LspLocation>> GetReferencesAsync(
        Document? document,
        Position position,
        bool includeDeclaration,
        CancellationToken cancellationToken)
    {
        (Document? sourceDocument, ISymbol? symbol) = await FindSymbolAsync(
            document,
            position,
            cancellationToken).ConfigureAwait(false);
        if (sourceDocument is null || symbol is null)
        {
            return [];
        }

        IEnumerable<ReferencedSymbol> referencedSymbols = await SymbolFinder.FindReferencesAsync(
            symbol,
            sourceDocument.Project.Solution,
            cancellationToken).ConfigureAwait(false);
        var locations = new HashSet<LspLocation>();
        foreach (ReferencedSymbol referencedSymbol in referencedSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (includeDeclaration)
            {
                int priorLocationCount = locations.Count;
                AddNavigationLocations(
                    locations,
                    sourceDocument.Project,
                    referencedSymbol.Definition.Locations);
                if (locations.Count == priorLocationCount)
                {
                    LspLocation? metadataLocation = await WorkspaceVirtualDocumentService
                        .GetMetadataLocationAsync(
                        sourceDocument.Project,
                        referencedSymbol.Definition,
                        cancellationToken).ConfigureAwait(false);
                    if (metadataLocation is not null)
                    {
                        locations.Add(metadataLocation);
                    }
                }
            }

            AddNavigationLocations(
                locations,
                sourceDocument.Project,
                referencedSymbol.Locations.Select(static reference => reference.Location));
            if (locations.Count >= MaximumNavigationLocations)
            {
                break;
            }
        }

        return OrderNavigationLocations(locations);
    }

    private static async Task<(Document? Document, ISymbol? Symbol)> FindSymbolAsync(
        Document? document,
        Position position,
        CancellationToken cancellationToken)
    {
        if (document is null)
        {
            return (null, null);
        }

        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        int offset = LspPositionConverter.GetOffset(text, position);
        SemanticModel semanticModel = await document
            .GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no semantic model.");
        ISymbol? symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            semanticModel,
            offset,
            document.Project.Solution.Workspace,
            cancellationToken).ConfigureAwait(false);
        return (document, symbol);
    }

    private static async Task<IReadOnlyList<LspLocation>> CreateNavigationLocationsAsync(
        Project project,
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        var locations = new HashSet<LspLocation>();
        AddNavigationLocations(locations, project, symbol.Locations);
        if (locations.Count == 0)
        {
            LspLocation? metadataLocation = await WorkspaceVirtualDocumentService
                .GetMetadataLocationAsync(project, symbol, cancellationToken)
                .ConfigureAwait(false);
            if (metadataLocation is not null)
            {
                locations.Add(metadataLocation);
            }
        }

        return OrderNavigationLocations(locations);
    }

    private static IReadOnlyList<LspLocation> OrderNavigationLocations(
        IEnumerable<LspLocation> locations) =>
    [
        .. locations
            .OrderBy(static location => location.Uri.ToString(), StringComparer.Ordinal)
            .ThenBy(static location => location.Range.Start.Line)
            .ThenBy(static location => location.Range.Start.Character)
            .Take(MaximumNavigationLocations)
    ];

    private static LspSelectionRange CreateSelectionRange(
        SyntaxNode root,
        SourceText text,
        int offset)
    {
        int tokenOffset = offset == text.Length && offset > 0
            ? offset - 1
            : offset;
        SyntaxToken token = root.FindToken(tokenOffset, findInsideTrivia: true);
        var spans = new List<TextSpan>();
        if (token.Span.Length > 0 &&
            token.Span.Start <= offset &&
            offset < token.Span.End)
        {
            spans.Add(token.Span);
        }
        else
        {
            spans.Add(new TextSpan(offset, 0));
        }

        foreach (TextSpan span in (token.Parent?.AncestorsAndSelf() ?? [])
            .Select(static node => node.Span)
            .Where(span =>
                span.Start <= offset &&
                offset <= span.End &&
                spans[^1] != span))
        {
            spans.Add(span);
        }

        LspSelectionRange? parent = null;
        for (int index = spans.Count - 1; index >= 0; index--)
        {
            LinePositionSpan lineSpan = text.Lines.GetLinePositionSpan(spans[index]);
            parent = new LspSelectionRange
            {
                Range = new LspRange(
                    new Position(lineSpan.Start.Line, lineSpan.Start.Character),
                    new Position(lineSpan.End.Line, lineSpan.End.Character)),
                Parent = parent
            };
        }

        return parent
            ?? throw new InvalidOperationException("No syntax selection range was produced.");
    }

    private static void AddDocumentHighlight(
        Dictionary<LspRange, LspDocumentHighlightKind> highlights,
        Project project,
        DocumentUri targetUri,
        RoslynLocation sourceLocation,
        LspDocumentHighlightKind kind)
    {
        if (highlights.Count >= MaximumNavigationLocations)
        {
            return;
        }

        LspLocation? location = ToLspLocation(project, sourceLocation);
        if (location is null || location.Uri != targetUri)
        {
            return;
        }

        if (!highlights.TryGetValue(location.Range, out LspDocumentHighlightKind existing) ||
            kind > existing)
        {
            highlights[location.Range] = kind;
        }
    }

    private static bool IsWrittenReference(
        SyntaxNode root,
        SemanticModel semanticModel,
        TextSpan sourceSpan,
        CancellationToken cancellationToken)
    {
        SyntaxNode node = root.FindNode(sourceSpan, getInnermostNodeForTie: true);
        IOperation? operation = semanticModel.GetOperation(node, cancellationToken);
        while (operation?.Parent is IOperation parent)
        {
            if (parent is IAssignmentOperation assignment &&
                ReferenceEquals(assignment.Target, operation))
            {
                return true;
            }

            if (parent is IIncrementOrDecrementOperation increment &&
                ReferenceEquals(increment.Target, operation))
            {
                return true;
            }

            if (parent is IArgumentOperation argument &&
                ReferenceEquals(argument.Value, operation) &&
                argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out)
            {
                return true;
            }

            operation = parent;
        }

        return false;
    }

    private static ITypeSymbol? GetSymbolType(ISymbol symbol) => symbol switch
    {
        IAliasSymbol alias => alias.Target as ITypeSymbol,
        ITypeSymbol type => type,
        IEventSymbol eventSymbol => eventSymbol.Type,
        IFieldSymbol field => field.Type,
        ILocalSymbol local => local.Type,
        IMethodSymbol { MethodKind: MethodKind.Constructor } constructor =>
            constructor.ContainingType,
        IMethodSymbol method => method.ReturnType,
        IParameterSymbol parameter => parameter.Type,
        IPropertySymbol property => property.Type,
        _ => null
    };

    private static void AddNavigationLocations(
        HashSet<LspLocation> target,
        Project project,
        IEnumerable<RoslynLocation> sourceLocations)
    {
        foreach (RoslynLocation sourceLocation in sourceLocations)
        {
            if (target.Count >= MaximumNavigationLocations)
            {
                return;
            }

            LspLocation? location = ToLspLocation(project, sourceLocation);
            if (location is not null)
            {
                target.Add(location);
            }
        }
    }

    private static LspLocation? ToLspLocation(Project project, RoslynLocation location)
    {
        string? path = location.SourceTree?.FilePath;
        if (!location.IsInSource || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        FileLinePositionSpan lineSpan = location.GetLineSpan();
        Document? document = project.Solution.GetDocument(location.SourceTree);
        DocumentUri uri;
        if (document is SourceGeneratedDocument generatedDocument &&
            !string.IsNullOrWhiteSpace(generatedDocument.Project.FilePath))
        {
            uri = VirtualDocumentUri.CreateGenerated(
                generatedDocument.Project.FilePath,
                generatedDocument.HintName);
        }
        else
        {
            uri = DocumentUri.FromFileSystemPath(path);
        }

        return new LspLocation
        {
            Uri = uri,
            Range = new LspRange(
                new Position(lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character),
                new Position(lineSpan.EndLinePosition.Line, lineSpan.EndLinePosition.Character))
        };
    }
}
