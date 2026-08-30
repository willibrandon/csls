using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Csls.Workspaces;

/// <summary>
/// Computes deterministic LSP folding ranges from public Roslyn syntax APIs.
/// </summary>
internal static class WorkspaceFoldingRangeService
{
    /// <summary>
    /// Gets bounded folding ranges for one immutable Roslyn document snapshot.
    /// </summary>
    /// <param name="document">The resolved Roslyn document, when present.</param>
    /// <param name="maximumRangeCount">The negotiated maximum result count.</param>
    /// <param name="lineFoldingOnly">Whether the client accepts line-only ranges.</param>
    /// <param name="includeCollapsedText">Whether collapsed display text is supported.</param>
    /// <param name="includeCommentKind">Whether the comment kind is supported.</param>
    /// <param name="includeImportsKind">Whether the imports kind is supported.</param>
    /// <param name="includeRegionKind">Whether the region kind is supported.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The ordered folding ranges for the current document contents.</returns>
    internal static async Task<IReadOnlyList<FoldingRange>> GetFoldingRangesAsync(
        Document? document,
        int maximumRangeCount,
        bool lineFoldingOnly,
        bool includeCollapsedText,
        bool includeCommentKind,
        bool includeImportsKind,
        bool includeRegionKind,
        CancellationToken cancellationToken)
    {
        if (document is null || maximumRangeCount <= 0)
        {
            return [];
        }

        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no syntax root.");
        var ranges = new FoldingRangeCollector(maximumRangeCount);
        AddBraceRanges(root, text, lineFoldingOnly, includeCollapsedText, ranges, cancellationToken);
        AddExpressionRanges(
            root,
            text,
            lineFoldingOnly,
            includeCollapsedText,
            ranges,
            cancellationToken);
        AddImportRanges(
            root,
            text,
            lineFoldingOnly,
            includeCollapsedText,
            includeImportsKind,
            ranges);
        AddCommentRanges(
            root,
            text,
            lineFoldingOnly,
            includeCollapsedText,
            includeCommentKind,
            ranges,
            cancellationToken);
        AddRegionRanges(
            root,
            text,
            lineFoldingOnly,
            includeCollapsedText,
            includeRegionKind,
            ranges,
            cancellationToken);
        AddConditionalDirectiveRanges(
            root,
            text,
            lineFoldingOnly,
            includeCollapsedText,
            includeRegionKind,
            ranges,
            cancellationToken);

        return ranges.ToArray();
    }

    private static void AddExpressionRanges(
        SyntaxNode root,
        SourceText text,
        bool lineFoldingOnly,
        bool includeCollapsedText,
        FoldingRangeCollector ranges,
        CancellationToken cancellationToken)
    {
        foreach (SyntaxNode node in root.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
            (SyntaxToken Open, SyntaxToken Close) = node switch
            {
                ArgumentListSyntax argumentList =>
                    (argumentList.OpenParenToken, argumentList.CloseParenToken),
                ParameterListSyntax parameterList =>
                    (parameterList.OpenParenToken, parameterList.CloseParenToken),
                CollectionExpressionSyntax collection =>
                    (collection.OpenBracketToken, collection.CloseBracketToken),
                _ => default
            };
            if (Open.RawKind != 0 && Close.RawKind != 0 &&
                !Open.IsMissing && !Close.IsMissing &&
                (!RequiresSeparatedBody(node) ||
                    text.Lines.GetLinePosition(Close.SpanStart).Line -
                    text.Lines.GetLinePosition(Open.SpanStart).Line >= 2))
            {
                AddRange(
                    ranges,
                    text,
                    TextSpan.FromBounds(Open.Span.End, Close.SpanStart),
                    kind: null,
                    collapsedText: includeCollapsedText ? "..." : null,
                    lineFoldingOnly,
                    preserveEndLine: true);
            }

            TextSpan? expressionSpan = node switch
            {
                ArrowExpressionClauseSyntax arrow => arrow.Span,
                InterpolatedStringExpressionSyntax interpolated => interpolated.Span,
                LiteralExpressionSyntax literal when IsMultilineString(literal, text) =>
                    literal.Span,
                _ => null
            };
            if (expressionSpan is TextSpan span)
            {
                AddRange(
                    ranges,
                    text,
                    span,
                    kind: null,
                    collapsedText: includeCollapsedText ? "..." : null,
                    lineFoldingOnly,
                    preserveEndLine: false);
            }
        }
    }

    private static void AddBraceRanges(
        SyntaxNode root,
        SourceText text,
        bool lineFoldingOnly,
        bool includeCollapsedText,
        FoldingRangeCollector ranges,
        CancellationToken cancellationToken)
    {
        foreach (SyntaxNode node in root.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyntaxToken openBrace = default;
            SyntaxToken closeBrace = default;
            foreach (SyntaxToken token in node.ChildTokens())
            {
                if (token.IsKind(SyntaxKind.OpenBraceToken) && openBrace.RawKind == 0)
                {
                    openBrace = token;
                }
                else if (token.IsKind(SyntaxKind.CloseBraceToken))
                {
                    closeBrace = token;
                }
            }

            if (openBrace.RawKind != 0 && closeBrace.RawKind != 0 &&
                !openBrace.IsMissing && !closeBrace.IsMissing)
            {
                AddRange(
                    ranges,
                    text,
                    TextSpan.FromBounds(openBrace.Span.End, closeBrace.SpanStart),
                    kind: null,
                    collapsedText: includeCollapsedText ? "..." : null,
                    lineFoldingOnly,
                    preserveEndLine: true);
            }
        }
    }

    private static void AddImportRanges(
        SyntaxNode root,
        SourceText text,
        bool lineFoldingOnly,
        bool includeCollapsedText,
        bool includeImportsKind,
        FoldingRangeCollector ranges)
    {
        foreach (SyntaxNode node in root.DescendantNodesAndSelf())
        {
            SyntaxList<UsingDirectiveSyntax> usings;
            SyntaxList<ExternAliasDirectiveSyntax> externs;
            switch (node)
            {
                case CompilationUnitSyntax compilationUnit:
                    usings = compilationUnit.Usings;
                    externs = compilationUnit.Externs;
                    break;
                case BaseNamespaceDeclarationSyntax namespaceDeclaration:
                    usings = namespaceDeclaration.Usings;
                    externs = namespaceDeclaration.Externs;
                    break;
                default:
                    continue;
            }

            if (usings.Count == 0 && externs.Count == 0)
            {
                continue;
            }

            int start = int.MaxValue;
            int end = 0;
            foreach (UsingDirectiveSyntax directive in usings)
            {
                start = Math.Min(start, directive.SpanStart);
                end = Math.Max(end, directive.Span.End);
            }

            foreach (ExternAliasDirectiveSyntax directive in externs)
            {
                start = Math.Min(start, directive.SpanStart);
                end = Math.Max(end, directive.Span.End);
            }

            AddRange(
                ranges,
                text,
                TextSpan.FromBounds(start, end),
                includeImportsKind ? FoldingRangeKind.Imports : null,
                includeCollapsedText ? "using ..." : null,
                lineFoldingOnly,
                preserveEndLine: false);
        }
    }

    private static void AddCommentRanges(
        SyntaxNode root,
        SourceText text,
        bool lineFoldingOnly,
        bool includeCollapsedText,
        bool includeCommentKind,
        FoldingRangeCollector ranges,
        CancellationToken cancellationToken)
    {
        SyntaxTrivia groupStart = default;
        SyntaxTrivia groupEnd = default;
        foreach (SyntaxTrivia trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) &&
                IsStandaloneComment(text, trivia))
            {
                if (groupEnd.RawKind != 0 &&
                    !IsWhitespaceOnly(text, groupEnd.Span.End, trivia.SpanStart))
                {
                    AddCommentGroup(
                        ranges,
                        text,
                        groupStart,
                        groupEnd,
                        lineFoldingOnly,
                        includeCollapsedText,
                        includeCommentKind);
                    groupStart = default;
                }

                if (groupStart.RawKind == 0)
                {
                    groupStart = trivia;
                }

                groupEnd = trivia;
                continue;
            }

            if (trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                AddRange(
                    ranges,
                    text,
                    TrimTrailingLineBreak(text, trivia.Span),
                    includeCommentKind ? FoldingRangeKind.Comment : null,
                    includeCollapsedText ? GetCollapsedLine(text, trivia.SpanStart) : null,
                    lineFoldingOnly,
                    preserveEndLine: false);
            }
        }

        if (groupEnd.RawKind != 0)
        {
            AddCommentGroup(
                ranges,
                text,
                groupStart,
                groupEnd,
                lineFoldingOnly,
                includeCollapsedText,
                includeCommentKind);
        }
    }

    private static void AddConditionalDirectiveRanges(
        SyntaxNode root,
        SourceText text,
        bool lineFoldingOnly,
        bool includeCollapsedText,
        bool includeRegionKind,
        FoldingRangeCollector ranges,
        CancellationToken cancellationToken)
    {
        var branchStack = new Stack<SyntaxTrivia>();
        foreach (SyntaxTrivia trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyntaxNode? structure = trivia.GetStructure();
            if (structure is IfDirectiveTriviaSyntax)
            {
                branchStack.Push(trivia);
            }
            else if (structure is ElifDirectiveTriviaSyntax or ElseDirectiveTriviaSyntax)
            {
                if (branchStack.TryPop(out SyntaxTrivia start))
                {
                    AddConditionalBranch(
                        ranges,
                        text,
                        start,
                        trivia,
                        lineFoldingOnly,
                        includeCollapsedText,
                        includeRegionKind);
                    branchStack.Push(trivia);
                }
            }
            else if (structure is EndIfDirectiveTriviaSyntax &&
                branchStack.TryPop(out SyntaxTrivia start))
            {
                AddConditionalBranch(
                    ranges,
                    text,
                    start,
                    trivia,
                    lineFoldingOnly,
                    includeCollapsedText,
                    includeRegionKind);
            }
        }
    }

    private static void AddConditionalBranch(
        FoldingRangeCollector ranges,
        SourceText text,
        SyntaxTrivia start,
        SyntaxTrivia end,
        bool lineFoldingOnly,
        bool includeCollapsedText,
        bool includeRegionKind)
    {
        AddRange(
            ranges,
            text,
            TextSpan.FromBounds(start.Span.End, end.SpanStart),
            includeRegionKind ? FoldingRangeKind.Region : null,
            includeCollapsedText ? "..." : null,
            lineFoldingOnly,
            preserveEndLine: true);
    }

    private static void AddCommentGroup(
        FoldingRangeCollector ranges,
        SourceText text,
        SyntaxTrivia start,
        SyntaxTrivia end,
        bool lineFoldingOnly,
        bool includeCollapsedText,
        bool includeCommentKind)
    {
        AddRange(
            ranges,
            text,
            TextSpan.FromBounds(start.SpanStart, end.Span.End),
            includeCommentKind ? FoldingRangeKind.Comment : null,
            includeCollapsedText ? GetCollapsedLine(text, start.SpanStart) : null,
            lineFoldingOnly,
            preserveEndLine: false);
    }

    private static void AddRegionRanges(
        SyntaxNode root,
        SourceText text,
        bool lineFoldingOnly,
        bool includeCollapsedText,
        bool includeRegionKind,
        FoldingRangeCollector ranges,
        CancellationToken cancellationToken)
    {
        var regionStack = new Stack<SyntaxTrivia>();
        foreach (SyntaxTrivia trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (trivia.GetStructure() is RegionDirectiveTriviaSyntax { IsActive: true })
            {
                regionStack.Push(trivia);
            }
            else if (trivia.GetStructure() is EndRegionDirectiveTriviaSyntax { IsActive: true } &&
                regionStack.TryPop(out SyntaxTrivia start))
            {
                AddRange(
                    ranges,
                    text,
                    TextSpan.FromBounds(start.SpanStart, trivia.Span.End),
                    includeRegionKind ? FoldingRangeKind.Region : null,
                    includeCollapsedText ? GetCollapsedLine(text, start.SpanStart) : null,
                    lineFoldingOnly,
                    preserveEndLine: true);
            }
        }
    }

    private static void AddRange(
        FoldingRangeCollector ranges,
        SourceText text,
        TextSpan span,
        string? kind,
        string? collapsedText,
        bool lineFoldingOnly,
        bool preserveEndLine)
    {
        LinePosition start = text.Lines.GetLinePosition(span.Start);
        LinePosition end = text.Lines.GetLinePosition(span.End);
        int endLine = lineFoldingOnly && preserveEndLine ? end.Line - 1 : end.Line;
        if (endLine <= start.Line)
        {
            return;
        }

        ranges.Add(new FoldingRange
        {
            StartLine = start.Line,
            StartCharacter = lineFoldingOnly ? null : start.Character,
            EndLine = endLine,
            EndCharacter = lineFoldingOnly ? null : end.Character,
            Kind = kind,
            CollapsedText = collapsedText
        });
    }

    private static bool IsStandaloneComment(SourceText text, SyntaxTrivia trivia)
    {
        TextLine line = text.Lines.GetLineFromPosition(trivia.SpanStart);
        for (int index = line.Start; index < trivia.SpanStart; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWhitespaceOnly(SourceText text, int start, int end)
    {
        for (int index = start; index < end; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsMultilineString(LiteralExpressionSyntax literal, SourceText text)
    {
        return literal.IsKind(SyntaxKind.StringLiteralExpression) &&
            !literal.ContainsDiagnostics &&
            text.Lines.GetLinePosition(literal.SpanStart).Line !=
            text.Lines.GetLinePosition(literal.Span.End).Line;
    }

    private static bool RequiresSeparatedBody(SyntaxNode node) =>
        node is ArgumentListSyntax or ParameterListSyntax;

    private static string GetCollapsedLine(SourceText text, int position) =>
        text.Lines.GetLineFromPosition(position).ToString().Trim();

    private static TextSpan TrimTrailingLineBreak(SourceText text, TextSpan span)
    {
        int end = span.End;
        while (end > span.Start && text[end - 1] is '\r' or '\n')
        {
            end--;
        }

        return TextSpan.FromBounds(span.Start, end);
    }

}
