using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Frozen;

namespace Csls.Workspaces;

/// <summary>
/// Converts Roslyn classifications into bounded non-overlapping LSP semantic-token data.
/// </summary>
internal static class WorkspaceSemanticTokensService
{
    private const int MaximumClassifiedSpans = 500_000;
    private const int MaximumSemanticTokens = 200_000;
    private const int StaticModifier = 1;
    private const int DeprecatedModifier = 2;
    private const int ReassignedModifier = 4;

    private static readonly FrozenDictionary<string, int> s_tokenTypeIndices =
        CSharpSemanticTokensLegend.TokenTypes
            .Select(static (name, index) => (name, index))
            .ToFrozenDictionary(
                static item => item.name,
                static item => item.index,
                StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> s_classificationTokenTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ClassificationTypeNames.Comment] = "comment",
            [ClassificationTypeNames.ExcludedCode] = "comment",
            [ClassificationTypeNames.Identifier] = "variable",
            [ClassificationTypeNames.Keyword] = "keyword",
            [ClassificationTypeNames.ControlKeyword] = "keyword",
            [ClassificationTypeNames.NumericLiteral] = "number",
            [ClassificationTypeNames.Operator] = "operator",
            [ClassificationTypeNames.OperatorOverloaded] = "operator",
            [ClassificationTypeNames.PreprocessorKeyword] = "macro",
            [ClassificationTypeNames.StringLiteral] = "string",
            [ClassificationTypeNames.PreprocessorText] = "macro",
            [ClassificationTypeNames.VerbatimStringLiteral] = "string",
            [ClassificationTypeNames.StringEscapeCharacter] = "string",
            [ClassificationTypeNames.ClassName] = "class",
            [ClassificationTypeNames.RecordClassName] = "class",
            [ClassificationTypeNames.DelegateName] = "type",
            [ClassificationTypeNames.EnumName] = "enum",
            [ClassificationTypeNames.InterfaceName] = "interface",
            [ClassificationTypeNames.ModuleName] = "namespace",
            [ClassificationTypeNames.StructName] = "struct",
            [ClassificationTypeNames.RecordStructName] = "struct",
            [ClassificationTypeNames.TypeParameterName] = "typeParameter",
            ["array name"] = "type",
            ["pointer name"] = "type",
            ["function pointer name"] = "type",
            [ClassificationTypeNames.FieldName] = "variable",
            [ClassificationTypeNames.EnumMemberName] = "enumMember",
            [ClassificationTypeNames.ConstantName] = "variable",
            [ClassificationTypeNames.LocalName] = "variable",
            [ClassificationTypeNames.ParameterName] = "parameter",
            [ClassificationTypeNames.MethodName] = "method",
            [ClassificationTypeNames.ExtensionMethodName] = "method",
            [ClassificationTypeNames.PropertyName] = "property",
            [ClassificationTypeNames.EventName] = "event",
            [ClassificationTypeNames.NamespaceName] = "namespace",
            [ClassificationTypeNames.LabelName] = "label",
            [ClassificationTypeNames.XmlDocCommentAttributeName] = "comment",
            [ClassificationTypeNames.XmlDocCommentAttributeQuotes] = "comment",
            [ClassificationTypeNames.XmlDocCommentAttributeValue] = "comment",
            [ClassificationTypeNames.XmlDocCommentCDataSection] = "comment",
            [ClassificationTypeNames.XmlDocCommentComment] = "comment",
            [ClassificationTypeNames.XmlDocCommentDelimiter] = "comment",
            [ClassificationTypeNames.XmlDocCommentEntityReference] = "comment",
            [ClassificationTypeNames.XmlDocCommentName] = "comment",
            [ClassificationTypeNames.XmlDocCommentProcessingInstruction] = "comment",
            [ClassificationTypeNames.XmlDocCommentText] = "comment",
            [ClassificationTypeNames.RegexComment] = "regexp",
            [ClassificationTypeNames.RegexCharacterClass] = "regexp",
            [ClassificationTypeNames.RegexAnchor] = "regexp",
            [ClassificationTypeNames.RegexQuantifier] = "regexp",
            [ClassificationTypeNames.RegexGrouping] = "regexp",
            [ClassificationTypeNames.RegexAlternation] = "regexp",
            [ClassificationTypeNames.RegexText] = "regexp",
            [ClassificationTypeNames.RegexSelfEscapedCharacter] = "regexp",
            [ClassificationTypeNames.RegexOtherEscape] = "regexp",
            ["json - comment"] = "comment",
            ["json - number"] = "number",
            ["json - string"] = "string",
            ["json - keyword"] = "keyword",
            ["json - operator"] = "operator",
            ["json - property name"] = "property"
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, int> s_classificationModifiers =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [ClassificationTypeNames.StaticSymbol] = StaticModifier,
            ["obsolete symbol"] = DeprecatedModifier,
            ["reassigned variable"] = ReassignedModifier
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Gets complete relative-encoded semantic tokens for one immutable document snapshot.
    /// </summary>
    /// <param name="document">The current Roslyn document, or null when it is unknown.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The complete five-integer token encoding.</returns>
    internal static async Task<IReadOnlyList<int>> GetSemanticTokensAsync(
        Document? document,
        CancellationToken cancellationToken)
    {
        if (document is null)
        {
            return [];
        }

        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (text.Length == 0)
        {
            return [];
        }

        IReadOnlyList<ClassifiedSpan> classifiedSpans =
        [
            .. await Classifier.GetClassifiedSpansAsync(
                document,
                new TextSpan(0, text.Length),
                cancellationToken).ConfigureAwait(false)
        ];
        if (classifiedSpans.Count > MaximumClassifiedSpans)
        {
            throw new InvalidOperationException(
                $"Semantic classification exceeded {MaximumClassifiedSpans} spans.");
        }

        var groups = new List<(TextSpan Span, int? TokenType, int Modifiers)>();
        var groupIndices = new Dictionary<TextSpan, int>();
        foreach (ClassifiedSpan classifiedSpan in classifiedSpans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TextSpan span = classifiedSpan.TextSpan;
            if (span.Length == 0)
            {
                continue;
            }

            if (!groupIndices.TryGetValue(span, out int groupIndex))
            {
                groupIndex = groups.Count;
                groupIndices.Add(span, groupIndex);
                groups.Add((span, null, 0));
            }

            (TextSpan groupSpan, int? tokenType, int modifiers) = groups[groupIndex];
            if (s_classificationModifiers.TryGetValue(
                classifiedSpan.ClassificationType,
                out int modifier))
            {
                modifiers |= modifier;
            }
            else if (tokenType is null &&
                s_classificationTokenTypes.TryGetValue(
                    classifiedSpan.ClassificationType,
                    out string? tokenTypeName))
            {
                tokenType = s_tokenTypeIndices[tokenTypeName];
            }

            groups[groupIndex] = (groupSpan, tokenType, modifiers);
        }

        var fragmentsByLine = new SortedDictionary<
            int,
            List<(int Start, int End, int TokenType, int Modifiers, int Specificity)>>();
        foreach ((TextSpan span, int? tokenType, int modifiers) in groups)
        {
            if (tokenType is null)
            {
                continue;
            }

            AddSingleLineFragments(
                text,
                span,
                tokenType.Value,
                modifiers,
                fragmentsByLine);
        }

        List<(int Line, int Start, int Length, int TokenType, int Modifiers)> tokens =
            NormalizeFragments(fragmentsByLine, cancellationToken);
        if (tokens.Count > MaximumSemanticTokens)
        {
            throw new InvalidOperationException(
                $"Semantic token output exceeded {MaximumSemanticTokens} tokens.");
        }

        int[] data = new int[tokens.Count * 5];
        int outputIndex = 0;
        int previousLine = 0;
        int previousStart = 0;
        foreach ((int line, int start, int length, int tokenType, int modifiers) in tokens)
        {
            int deltaLine = line - previousLine;
            data[outputIndex++] = deltaLine;
            data[outputIndex++] = deltaLine == 0 ? start - previousStart : start;
            data[outputIndex++] = length;
            data[outputIndex++] = tokenType;
            data[outputIndex++] = modifiers;
            previousLine = line;
            previousStart = start;
        }

        return data;
    }

    private static void AddSingleLineFragments(
        SourceText text,
        TextSpan span,
        int tokenType,
        int modifiers,
        SortedDictionary<
            int,
            List<(int Start, int End, int TokenType, int Modifiers, int Specificity)>> fragmentsByLine)
    {
        LinePositionSpan positionSpan = text.Lines.GetLinePositionSpan(span);
        for (int lineNumber = positionSpan.Start.Line;
            lineNumber <= positionSpan.End.Line;
            lineNumber++)
        {
            TextLine line = text.Lines[lineNumber];
            int absoluteStart = lineNumber == positionSpan.Start.Line ? span.Start : line.Start;
            int absoluteEnd = lineNumber == positionSpan.End.Line ? span.End : line.End;
            absoluteStart = Math.Min(Math.Max(absoluteStart, line.Start), line.End);
            absoluteEnd = Math.Min(Math.Max(absoluteEnd, line.Start), line.End);
            if (absoluteEnd <= absoluteStart)
            {
                continue;
            }

            if (!fragmentsByLine.TryGetValue(lineNumber, out List<(
                int Start,
                int End,
                int TokenType,
                int Modifiers,
                int Specificity)>? fragments))
            {
                fragments = [];
                fragmentsByLine.Add(lineNumber, fragments);
            }

            fragments.Add((
                absoluteStart - line.Start,
                absoluteEnd - line.Start,
                tokenType,
                modifiers,
                span.Length));
        }
    }

    private static List<(int Line, int Start, int Length, int TokenType, int Modifiers)>
        NormalizeFragments(
            SortedDictionary<
                int,
                List<(int Start, int End, int TokenType, int Modifiers, int Specificity)>> fragmentsByLine,
            CancellationToken cancellationToken)
    {
        var tokens = new List<(int Line, int Start, int Length, int TokenType, int Modifiers)>();
        foreach ((int lineNumber, List<(
            int Start,
            int End,
            int TokenType,
            int Modifiers,
            int Specificity)> fragments) in fragmentsByLine)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<int> boundaries =
            [
                .. fragments
                    .SelectMany(static fragment => new[] { fragment.Start, fragment.End })
                    .Distinct()
                    .Order()
            ];
            for (int boundaryIndex = 0; boundaryIndex + 1 < boundaries.Count; boundaryIndex++)
            {
                int start = boundaries[boundaryIndex];
                int end = boundaries[boundaryIndex + 1];
                (int Start, int End, int TokenType, int Modifiers, int Specificity)? selected = null;
                foreach ((int fragmentStart,
                    int fragmentEnd,
                    int tokenType,
                    int modifiers,
                    int specificity) in fragments)
                {
                    if (fragmentStart > start || fragmentEnd < end)
                    {
                        continue;
                    }

                    if (selected is null ||
                        specificity < selected.Value.Specificity ||
                        (specificity == selected.Value.Specificity &&
                            tokenType < selected.Value.TokenType))
                    {
                        selected = (
                            fragmentStart,
                            fragmentEnd,
                            tokenType,
                            modifiers,
                            specificity);
                    }
                }

                if (selected is null)
                {
                    continue;
                }

                int length = end - start;
                if (tokens.Count > 0)
                {
                    (int priorLine,
                        int priorStart,
                        int priorLength,
                        int priorTokenType,
                        int priorModifiers) = tokens[^1];
                    if (priorLine == lineNumber &&
                        priorStart + priorLength == start &&
                        priorTokenType == selected.Value.TokenType &&
                        priorModifiers == selected.Value.Modifiers)
                    {
                        tokens[^1] = (
                            priorLine,
                            priorStart,
                            priorLength + length,
                            priorTokenType,
                            priorModifiers);
                        continue;
                    }
                }

                tokens.Add((
                    lineNumber,
                    start,
                    length,
                    selected.Value.TokenType,
                    selected.Value.Modifiers));
            }
        }

        return tokens;
    }
}
