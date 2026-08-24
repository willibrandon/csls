using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;

namespace Csls.Workspaces;

/// <summary>
/// Resolves Roslyn quick info and semantic fallback content for one document snapshot.
/// </summary>
internal static class WorkspaceHoverService
{
    /// <summary>
    /// Gets stable documentation and its generated or source document span.
    /// </summary>
    /// <param name="document">The immutable Roslyn document snapshot.</param>
    /// <param name="offset">The zero-based UTF-16 document offset.</param>
    /// <param name="supportsMarkdown">Whether the receiving client accepts Markdown.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The hover content and document span, or null when no symbol is present.</returns>
    internal static async Task<(MarkupContent Content, TextSpan Span)?> GetAsync(
        Document document,
        int offset,
        bool supportsMarkdown,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var quickInfoService = QuickInfoService.GetService(document);
        if (quickInfoService is not null)
        {
            QuickInfoItem? quickInfo = await quickInfoService
                .GetQuickInfoAsync(document, offset, cancellationToken)
                .ConfigureAwait(false);
            if (quickInfo is not null)
            {
                MarkupContent content = QuickInfoMarkupFormatter.Format(
                    quickInfo,
                    supportsMarkdown);
                if (!string.IsNullOrWhiteSpace(content.Value))
                {
                    (ISymbol? quickInfoSymbol, Compilation compilation, _) =
                        await ResolveSymbolAsync(
                            document,
                            offset,
                            cancellationToken).ConfigureAwait(false);
                    MarkupContent? supplemental = quickInfoSymbol is null
                        ? null
                        : SymbolDocumentationFormatter
                            .FormatSymbol(
                                quickInfoSymbol,
                                compilation,
                                supportsMarkdown,
                                cancellationToken)
                            .SupplementalDocumentation;
                    return (
                        TaggedTextMarkupFormatter.Combine(content, supplemental),
                        quickInfo.Span);
                }
            }
        }

        (ISymbol? symbol, _, TextSpan span) = await ResolveSymbolAsync(
            document,
            offset,
            cancellationToken).ConfigureAwait(false);
        if (symbol is null)
        {
            return null;
        }

        string display = symbol.ToDisplayString();
        return (
            new MarkupContent
            {
                Kind = supportsMarkdown ? "markdown" : "plaintext",
                Value = supportsMarkdown
                    ? $"```csharp{Environment.NewLine}{display}{Environment.NewLine}```"
                    : display
            },
            span);
    }

    private static async Task<(
        ISymbol? Symbol,
        Compilation Compilation,
        TextSpan Span)> ResolveSymbolAsync(
        Document document,
        int offset,
        CancellationToken cancellationToken)
    {
        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no syntax root.");
        SyntaxToken token = root.FindToken(offset, findInsideTrivia: true);
        SemanticModel semanticModel = await document
            .GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no semantic model.");
        ISymbol? symbol = token.Parent is null
            ? null
            : semanticModel.GetSymbolInfo(token.Parent, cancellationToken).Symbol
                ?? semanticModel.GetDeclaredSymbol(token.Parent, cancellationToken);
        return (symbol, semanticModel.Compilation, token.Span);
    }
}
