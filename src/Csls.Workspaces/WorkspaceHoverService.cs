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
    /// Gets stable Markdown and its generated or source document span.
    /// </summary>
    /// <param name="document">The immutable Roslyn document snapshot.</param>
    /// <param name="offset">The zero-based UTF-16 document offset.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The hover content and document span, or null when no symbol is present.</returns>
    internal static async Task<(string Markdown, TextSpan Span)?> GetAsync(
        Document document,
        int offset,
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
                string markdown = QuickInfoMarkdownFormatter.Format(quickInfo);
                if (!string.IsNullOrWhiteSpace(markdown))
                {
                    return (markdown, quickInfo.Span);
                }
            }
        }

        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no syntax root.");
        SyntaxToken token = root.FindToken(offset, findInsideTrivia: true);
        if (token.Parent is not SyntaxNode parent)
        {
            return null;
        }

        SemanticModel semanticModel = await document
            .GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no semantic model.");
        ISymbol? symbol = semanticModel.GetSymbolInfo(parent, cancellationToken).Symbol
            ?? semanticModel.GetDeclaredSymbol(parent, cancellationToken);
        return symbol is null
            ? null
            : ($"```csharp{Environment.NewLine}{symbol.ToDisplayString()}{Environment.NewLine}```", token.Span);
    }
}
