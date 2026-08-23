using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using LspRange = Csls.Protocol.Range;

namespace Csls.Workspaces;

/// <summary>
/// Finds paired XML documentation names that C# clients can edit together.
/// </summary>
internal static class WorkspaceLinkedEditingService
{
    /// <summary>
    /// Gets linked editing ranges at one UTF-16 document position.
    /// </summary>
    /// <param name="document">The resolved Roslyn document, when present.</param>
    /// <param name="position">The target UTF-16 position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The paired XML names, or <see langword="null"/> when none apply.</returns>
    internal static async Task<LinkedEditingRanges?> GetLinkedEditingRangesAsync(
        Document? document,
        Position position,
        CancellationToken cancellationToken)
    {
        if (document is null)
        {
            return null;
        }

        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        SyntaxNode? root = await document
            .GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false);
        if (root is null)
        {
            return null;
        }

        int offset = LspPositionConverter.GetOffset(text, position);
        SyntaxNode? node = root.FindToken(offset, findInsideTrivia: true).Parent;
        while (node is not null)
        {
            if (node is XmlElementSyntax element &&
                TryCreateRanges(element, text, offset, out LinkedEditingRanges? ranges))
            {
                return ranges;
            }

            node = node.Parent;
        }

        return null;
    }

    private static bool TryCreateRanges(
        XmlElementSyntax element,
        SourceText text,
        int offset,
        out LinkedEditingRanges? ranges)
    {
        TextSpan start = element.StartTag.Name.Span;
        TextSpan end = element.EndTag.Name.Span;
        if (start.Length == 0 ||
            start.Length != end.Length ||
            start.End > end.Start ||
            !ContainsPosition(start, offset) && !ContainsPosition(end, offset) ||
            !ContentEquals(text, start, end))
        {
            ranges = null;
            return false;
        }

        ranges = new LinkedEditingRanges
        {
            Ranges = [ToRange(text, start), ToRange(text, end)]
        };
        return true;
    }

    private static bool ContainsPosition(TextSpan span, int offset) =>
        offset >= span.Start && offset <= span.End;

    private static bool ContentEquals(SourceText text, TextSpan left, TextSpan right)
    {
        for (int index = 0; index < left.Length; index++)
        {
            if (text[left.Start + index] != text[right.Start + index])
            {
                return false;
            }
        }

        return true;
    }

    private static LspRange ToRange(SourceText text, TextSpan span)
    {
        LinePositionSpan lineSpan = text.Lines.GetLinePositionSpan(span);
        return new LspRange(
            new Position(lineSpan.Start.Line, lineSpan.Start.Character),
            new Position(lineSpan.End.Line, lineSpan.End.Character));
    }
}
