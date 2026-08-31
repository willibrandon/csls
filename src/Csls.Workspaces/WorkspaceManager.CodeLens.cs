using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using System.Text.Json;
using LspLocation = Csls.Protocol.Location;
using LspRange = Csls.Protocol.Range;

namespace Csls.Workspaces;

public sealed partial class WorkspaceManager
{
    private const int MaximumExactCodeLensReferences = 99;

    /// <summary>
    /// Gets unresolved reference-count annotations through syntax-only declaration discovery.
    /// </summary>
    /// <param name="parameters">The target source document.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The ordered unresolved declaration annotations.</returns>
    public async Task<IReadOnlyList<CodeLens>> GetCodeLensesAsync(
        CodeLensParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Document? document = FindCurrentDocument(parameters.TextDocument.Uri);
        if (document is null)
        {
            return [];
        }

        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no syntax root.");
        long generation = Generation;
        var lenses = new List<CodeLens>();
        foreach (SyntaxNode node in root.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetCodeLensIdentifier(node, out SyntaxToken identifier))
            {
                continue;
            }

            LspRange range = ToLspRange(text, identifier.Span);
            lenses.Add(new CodeLens
            {
                Range = range,
                Data = new CodeLensData
                {
                    Generation = generation,
                    Uri = parameters.TextDocument.Uri,
                    DeclarationRange = range
                }
            });
        }

        return lenses;
    }

    /// <summary>
    /// Resolves one reference-count annotation against its immutable workspace generation.
    /// </summary>
    /// <param name="codeLens">The unresolved annotation returned by this workspace.</param>
    /// <param name="commandIdentifier">The reference-popup command understood by the client.</param>
    /// <param name="includeLocations">Whether the command requires embedded reference locations.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The annotation populated with a current count and executable command.</returns>
    public async Task<CodeLens> ResolveCodeLensAsync(
        CodeLens codeLens,
        string commandIdentifier,
        bool includeLocations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(codeLens);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandIdentifier);
        CodeLensData data = codeLens.Data
            ?? throw new InvalidDataException("The code lens contains no resolve data.");
        if (data.Generation != Generation)
        {
            throw new InvalidOperationException(
                "The code lens belongs to a retired workspace generation.");
        }

        Document document = FindCurrentDocument(data.Uri)
            ?? throw new InvalidDataException("The code-lens source document is no longer loaded.");
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no syntax root.");
        SyntaxNode declaration = FindCodeLensDeclaration(root, text, data.DeclarationRange)
            ?? throw new InvalidDataException("The code-lens declaration is no longer present.");
        SemanticModel semanticModel = await document.GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no semantic model.");
        ISymbol symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken)
            ?? throw new InvalidDataException("The code-lens declaration has no declared symbol.");

        int searchCap = includeLocations ? 0 : MaximumExactCodeLensReferences;
        using var progress = new CodeLensReferenceProgress(
            symbol,
            declaration,
            searchCap,
            cancellationToken);
        try
        {
            await SymbolFinder.FindReferencesAsync(
                symbol,
                document.Project.Solution,
                progress,
                documents: null,
                progress.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            progress.SearchCapReached && !cancellationToken.IsCancellationRequested)
        {
        }

        int count = progress.ReferenceCount;
        bool capped = progress.SearchCapReached || count > MaximumExactCodeLensReferences;
        int displayedCount = capped ? MaximumExactCodeLensReferences : count;
        string title = displayedCount == 1
            ? "1 reference"
            : $"{displayedCount}{(capped ? "+" : string.Empty)} references";
        List<JsonElement> arguments =
        [
            JsonSerializer.SerializeToElement(data.Uri.ToString()),
            JsonSerializer.SerializeToElement(
                data.DeclarationRange.Start,
                LspJsonSerializerContext.Default.Position)
        ];
        if (includeLocations)
        {
            IReadOnlyList<LspLocation> locations =
            [
                .. progress.Locations
                    .Select(ToLspLocation)
                    .OfType<LspLocation>()
                    .Distinct()
            ];
            arguments.Add(JsonSerializer.SerializeToElement(
                locations,
                LspJsonSerializerContext.Default.IReadOnlyListLocation));
        }

        return codeLens with
        {
            Command = new LspCommand
            {
                Title = title,
                Command = commandIdentifier,
                Arguments = arguments
            }
        };
    }

    private static SyntaxNode? FindCodeLensDeclaration(
        SyntaxNode root,
        SourceText text,
        LspRange range)
    {
        int start = LspPositionConverter.GetOffset(text, range.Start);
        SyntaxToken token = root.FindToken(start);
        return token.Parent?
            .AncestorsAndSelf()
            .FirstOrDefault(node =>
                TryGetCodeLensIdentifier(node, out SyntaxToken identifier) &&
                identifier.SpanStart == start &&
                ToLspRange(text, identifier.Span) == range);
    }

    private static bool TryGetCodeLensIdentifier(
        SyntaxNode node,
        out SyntaxToken identifier)
    {
        identifier = node switch
        {
            BaseTypeDeclarationSyntax declaration => declaration.Identifier,
            DelegateDeclarationSyntax declaration => declaration.Identifier,
            PropertyDeclarationSyntax declaration => declaration.Identifier,
            MethodDeclarationSyntax declaration => declaration.Identifier,
            ConstructorDeclarationSyntax declaration => declaration.Identifier,
            DestructorDeclarationSyntax declaration => declaration.Identifier,
            EventDeclarationSyntax declaration => declaration.Identifier,
            EnumMemberDeclarationSyntax declaration => declaration.Identifier,
            VariableDeclaratorSyntax declaration when
                declaration.Parent?.Parent is FieldDeclarationSyntax or
                    EventFieldDeclarationSyntax => declaration.Identifier,
            _ => default
        };
        return identifier != default;
    }
}
