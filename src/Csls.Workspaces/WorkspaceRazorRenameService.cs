using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using LspRange = Csls.Protocol.Range;
using LspTextEdit = Csls.Protocol.TextEdit;
using RoslynLocation = Microsoft.CodeAnalysis.Location;
using RoslynSymbolKind = Microsoft.CodeAnalysis.SymbolKind;

namespace Csls.Workspaces;

/// <summary>
/// Resolves Razor rename targets and maps generated C# rename locations to Razor source.
/// </summary>
internal static class WorkspaceRazorRenameService
{
    private const int MaximumMappedEdits = 10_000;

    /// <summary>
    /// Resolves a Razor source position to its generated document, symbol, and source range.
    /// </summary>
    /// <param name="solution">The immutable current solution.</param>
    /// <param name="path">The absolute Razor document path.</param>
    /// <param name="position">The target Razor source position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The mapped document, symbol, and Razor source range when available.</returns>
    internal static async Task<(
        RazorMappedDocument? MappedDocument,
        ISymbol? Symbol,
        LspRange? Range)> ResolveTargetAsync(
        Solution solution,
        string path,
        Position position,
        CancellationToken cancellationToken)
    {
        RazorMappedDocument? mappedDocument = await WorkspaceRazorMappingService.ResolveAsync(
            solution,
            path,
            position,
            cancellationToken).ConfigureAwait(false);
        if (mappedDocument is null)
        {
            return (null, null, null);
        }

        SemanticModel semanticModel = await mappedDocument.Document
            .GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no Razor semantic model.");
        ISymbol? symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            semanticModel,
            mappedDocument.GeneratedOffset,
            mappedDocument.Document.Project.Solution.Workspace,
            cancellationToken).ConfigureAwait(false);
        if (symbol is null)
        {
            return (mappedDocument, null, null);
        }

        SyntaxNode root = await mappedDocument.Document
            .GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no Razor syntax root.");
        int tokenOffset = Math.Clamp(
            mappedDocument.GeneratedOffset == root.FullSpan.End
                ? mappedDocument.GeneratedOffset - 1
                : mappedDocument.GeneratedOffset,
            0,
            Math.Max(0, root.FullSpan.End - 1));
        SyntaxToken token = root.FindToken(tokenOffset, findInsideTrivia: true);
        return WorkspaceRazorMappingService.TryMapRange(
            mappedDocument,
            token.Span,
            cancellationToken,
            out LspRange range)
                ? (mappedDocument, symbol, range)
                : (mappedDocument, symbol, null);
    }

    /// <summary>
    /// Finds all generated C# rename locations that map to current Razor source documents.
    /// </summary>
    /// <param name="solution">The immutable current solution.</param>
    /// <param name="symbol">The normalized Roslyn symbol being renamed.</param>
    /// <param name="newName">The validated replacement identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>Mapped Razor edits indexed by absolute source path.</returns>
    internal static async Task<IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>>>
        GetMappedEditsAsync(
            Solution solution,
            ISymbol symbol,
            string newName,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        var additionalDocuments = new Dictionary<string, TextDocument>(PathComparer);
        IEnumerable<TextDocument> razorDocuments = solution.Projects
            .SelectMany(static project => project.AdditionalDocuments)
            .Where(static document =>
                !string.IsNullOrWhiteSpace(document.FilePath) &&
                WorkspaceRazorDiagnosticService.IsRazorDocument(document.FilePath));
        foreach (TextDocument document in razorDocuments)
        {
            additionalDocuments.TryAdd(document.FilePath!, document);
        }

        if (additionalDocuments.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<LspTextEdit>>(PathComparer);
        }

        IEnumerable<ReferencedSymbol> referencedSymbols = await SymbolFinder.FindReferencesAsync(
            symbol,
            solution,
            cancellationToken).ConfigureAwait(false);
        var locations = new HashSet<RoslynLocation>();
        var renamedSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default)
        {
            symbol
        };
        AddDefinitionLocations(locations, symbol, symbol);
        foreach (ReferencedSymbol referencedSymbol in referencedSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            renamedSymbols.Add(referencedSymbol.Definition);
            AddDefinitionLocations(locations, referencedSymbol.Definition, symbol);
            locations.UnionWith(referencedSymbol.Locations.Select(static item => item.Location));
        }

        var editsByPath = new Dictionary<string, List<LspTextEdit>>(PathComparer);
        int mappedEditCount = 0;
        foreach (RoslynLocation location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyntaxTree? sourceTree = location.SourceTree;
            if (!location.IsInSource || sourceTree is null)
            {
                continue;
            }

            FileLinePositionSpan mappedSpan = sourceTree.GetMappedLineSpan(
                location.SourceSpan,
                cancellationToken);
            if (!mappedSpan.IsValid ||
                string.IsNullOrWhiteSpace(mappedSpan.Path) ||
                !additionalDocuments.TryGetValue(mappedSpan.Path, out TextDocument? document))
            {
                continue;
            }

            SourceText text = await document
                .GetTextAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!WorkspaceRazorMappingService.TryGetTextSpan(
                    text,
                    mappedSpan.Span,
                    out TextSpan sourceSpan) ||
                !MatchesIdentifier(text, sourceSpan, symbol.Name))
            {
                continue;
            }

            await ValidateReplacementAsync(
                solution,
                sourceTree,
                location.SourceSpan.Start,
                symbol,
                renamedSymbols,
                newName,
                cancellationToken).ConfigureAwait(false);

            List<LspTextEdit> edits = editsByPath.GetValueOrDefault(mappedSpan.Path) ?? [];
            if (edits.Count == 0)
            {
                editsByPath.Add(mappedSpan.Path, edits);
            }

            var edit = new LspTextEdit
            {
                Range = new LspRange(
                    new Position(
                        mappedSpan.StartLinePosition.Line,
                        mappedSpan.StartLinePosition.Character),
                    new Position(
                        mappedSpan.EndLinePosition.Line,
                        mappedSpan.EndLinePosition.Character)),
                NewText = newName
            };
            if (!edits.Contains(edit))
            {
                edits.Add(edit);
                mappedEditCount++;
                if (mappedEditCount > MaximumMappedEdits)
                {
                    throw new InvalidOperationException(
                        $"The Razor rename exceeds {MaximumMappedEdits} text edits.");
                }
            }
        }

        var result = new Dictionary<string, IReadOnlyList<LspTextEdit>>(PathComparer);
        foreach ((string path, List<LspTextEdit> edits) in editsByPath)
        {
            result.Add(
                path,
                [
                .. edits
                    .OrderBy(static edit => edit.Range.Start.Line)
                    .ThenBy(static edit => edit.Range.Start.Character)
                ]);
        }

        return result;
    }

    private static async Task ValidateReplacementAsync(
        Solution solution,
        SyntaxTree sourceTree,
        int position,
        ISymbol symbol,
        HashSet<ISymbol> renamedSymbols,
        string newName,
        CancellationToken cancellationToken)
    {
        Document? document = solution.GetDocument(sourceTree);
        if (document is null)
        {
            return;
        }

        SemanticModel semanticModel = await document
            .GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no Razor semantic model.");
        string semanticName = newName[0] == '@' ? newName[1..] : newName;
        INamespaceOrTypeSymbol? container = symbol.Kind switch
        {
            RoslynSymbolKind.NamedType or RoslynSymbolKind.Namespace =>
                symbol.ContainingNamespace,
            RoslynSymbolKind.Event or
                RoslynSymbolKind.Field or
                RoslynSymbolKind.Method or
                RoslynSymbolKind.Property or
                RoslynSymbolKind.TypeParameter => symbol.ContainingType,
            _ => null
        };
        bool conflicts = semanticModel
            .LookupSymbols(position, container, semanticName)
            .Any(candidate => !renamedSymbols.Contains(candidate));
        if (conflicts)
        {
            throw new InvalidOperationException(
                $"Renaming '{symbol.Name}' to '{newName}' would change a Razor symbol binding.");
        }
    }

    private static void AddDefinitionLocations(
        HashSet<RoslynLocation> locations,
        ISymbol definition,
        ISymbol originalSymbol)
    {
        if (definition.Name == originalSymbol.Name &&
            definition is not IMethodSymbol { AssociatedSymbol: not null })
        {
            locations.UnionWith(definition.Locations);
        }
    }

    private static bool MatchesIdentifier(
        SourceText text,
        TextSpan span,
        string identifier)
    {
        string value = text.ToString(span);
        SyntaxToken token = SyntaxFactory.ParseToken(value);
        return token.IsKind(SyntaxKind.IdentifierToken) &&
            string.Equals(token.ValueText, identifier, StringComparison.Ordinal);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
