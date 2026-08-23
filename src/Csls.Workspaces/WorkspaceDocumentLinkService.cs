using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using LspRange = Csls.Protocol.Range;

namespace Csls.Workspaces;

/// <summary>
/// Resolves active C# file directives to existing local resources.
/// </summary>
internal static class WorkspaceDocumentLinkService
{
    private const int MaximumDocumentLinks = 1_000;

    /// <summary>
    /// Gets bounded links for active file-bearing directives in one document.
    /// </summary>
    /// <param name="document">The resolved Roslyn document, when present.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The ordered links whose local targets currently exist.</returns>
    internal static async Task<IReadOnlyList<DocumentLink>> GetDocumentLinksAsync(
        Document? document,
        CancellationToken cancellationToken)
    {
        if (document?.FilePath is not string documentPath)
        {
            return [];
        }

        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no syntax root.");
        string? documentDirectory = Path.GetDirectoryName(documentPath);
        if (string.IsNullOrWhiteSpace(documentDirectory))
        {
            return [];
        }

        var links = new List<DocumentLink>();
        foreach (SyntaxTrivia trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyntaxToken file = trivia.GetStructure() switch
            {
                LineDirectiveTriviaSyntax { IsActive: true } directive => directive.File,
                LoadDirectiveTriviaSyntax { IsActive: true } directive => directive.File,
                ReferenceDirectiveTriviaSyntax { IsActive: true } directive => directive.File,
                _ => default
            };
            if (file.RawKind == 0 || file.IsMissing || string.IsNullOrWhiteSpace(file.ValueText))
            {
                continue;
            }

            string? targetPath = ResolveExistingPath(documentDirectory, file.ValueText);
            if (targetPath is null || !TryGetLinkSpan(file, out TextSpan linkSpan))
            {
                continue;
            }

            LinePositionSpan lineSpan = text.Lines.GetLinePositionSpan(linkSpan);
            links.Add(new DocumentLink
            {
                Range = new LspRange(
                    new Position(lineSpan.Start.Line, lineSpan.Start.Character),
                    new Position(lineSpan.End.Line, lineSpan.End.Character)),
                Target = DocumentUri.FromFileSystemPath(targetPath)
            });
            if (links.Count >= MaximumDocumentLinks)
            {
                break;
            }
        }

        return links;
    }

    private static string? ResolveExistingPath(string documentDirectory, string reference)
    {
        try
        {
            string path = Path.GetFullPath(reference, documentDirectory);
            return File.Exists(path) ? path : null;
        }
        catch (Exception exception) when (exception is ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool TryGetLinkSpan(SyntaxToken token, out TextSpan linkSpan)
    {
        string text = token.Text;
        int firstQuote = text.IndexOf('"', StringComparison.Ordinal);
        int lastQuote = text.LastIndexOf('"');
        if (firstQuote < 0 || lastQuote <= firstQuote)
        {
            linkSpan = default;
            return false;
        }

        linkSpan = TextSpan.FromBounds(
            token.SpanStart + firstQuote + 1,
            token.SpanStart + lastQuote);
        return true;
    }
}
