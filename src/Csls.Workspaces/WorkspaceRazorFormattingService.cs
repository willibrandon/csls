using Csls.Protocol;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace Csls.Workspaces;

/// <summary>
/// Formats Razor source using public Razor compiler and Roslyn syntax APIs.
/// </summary>
internal static class WorkspaceRazorFormattingService
{
    private static readonly string[] s_controlKeywords =
    [
        "if",
        "for",
        "foreach",
        "while",
        "switch",
        "using",
        "lock",
        "catch"
    ];

    private static readonly string[] s_rawTextElements =
    [
        "pre",
        "script",
        "style",
        "textarea"
    ];

    private static readonly string[] s_voidElements =
    [
        "area",
        "base",
        "br",
        "col",
        "embed",
        "hr",
        "img",
        "input",
        "link",
        "meta",
        "param",
        "source",
        "track",
        "wbr"
    ];

    /// <summary>
    /// Formats one current Razor source snapshot using editor indentation preferences.
    /// </summary>
    /// <param name="text">The immutable Razor source text.</param>
    /// <param name="path">The absolute Razor document path.</param>
    /// <param name="options">The editor formatting preferences.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The formatted immutable source text.</returns>
    internal static SourceText Format(
        SourceText text,
        string path,
        FormattingOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);

        string source = text.ToString();
        RazorFileKind fileKind = FileKinds.GetFileKindFromPath(path);
        var parserOptions = RazorParserOptions.Create(
            RazorLanguageVersion.Latest,
            fileKind,
            static builder => builder.UseRoslynTokenizer = true);
        _ = RazorSyntaxTree.Parse(
            RazorSourceDocument.Create(source, path),
            parserOptions,
            cancellationToken);

        var builder = new StringBuilder(source.Length);
        var markupState = new RazorMarkupFormattingState();
        int csharpDepth = 0;
        int caseIndent = 0;
        bool inRazorComment = false;

        for (int lineIndex = 0; lineIndex < text.Lines.Count; lineIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TextLine textLine = text.Lines[lineIndex];
            string line = source[textLine.Start..textLine.End];

            if (markupState.RawTextElement is not null)
            {
                builder.Append(line);
                AppendLineBreak(builder, source, textLine);
                if (ContainsEndTag(line, markupState.RawTextElement))
                {
                    markupState.HtmlDepth = Math.Max(0, markupState.HtmlDepth - 1);
                    markupState.RawTextElement = null;
                }

                continue;
            }

            string content = line.TrimStart(' ', '\t');
            if (content.Length == 0)
            {
                AppendLineBreak(builder, source, textLine);
                continue;
            }

            if (inRazorComment)
            {
                builder.Append(line);
                AppendLineBreak(builder, source, textLine);
                inRazorComment = !content.Contains("*@", StringComparison.Ordinal);
                continue;
            }

            bool startsRazorComment = content.StartsWith("@*", StringComparison.Ordinal);
            bool caseLabel = IsCaseLabel(content);
            int leadingCsharpClosures = CountLeadingClosures(content);
            if (caseLabel || leadingCsharpClosures > 0)
            {
                caseIndent = 0;
            }

            int displayHtmlDepth = content.StartsWith("</", StringComparison.Ordinal)
                ? Math.Max(0, markupState.HtmlDepth - 1)
                : markupState.HtmlDepth;
            int displayCsharpDepth = Math.Max(0, csharpDepth - leadingCsharpClosures);
            int indentationLevel = displayHtmlDepth + displayCsharpDepth + caseIndent;

            if (markupState.InTag)
            {
                builder.Append(CreateAlignmentIndentation(markupState.ContinuationColumn, options));
            }
            else
            {
                builder.Append(CreateIndentation(indentationLevel, options));
            }

            bool csharpLine = IsCSharpLine(content, csharpDepth);
            string formattedContent = csharpLine
                ? FormatCSharpLine(content)
                : FormatExplicitExpressions(content);
            builder.Append(formattedContent);
            AppendLineBreak(builder, source, textLine);

            if (startsRazorComment &&
                !content.Contains("*@", StringComparison.Ordinal))
            {
                inRazorComment = true;
                continue;
            }

            if (!csharpLine)
            {
                AnalyzeMarkup(
                    formattedContent,
                    displayHtmlDepth + displayCsharpDepth + caseIndent,
                    options,
                    ref markupState);
            }

            if (csharpLine)
            {
                (int openings, int closures) = CountCSharpBraces(formattedContent);
                csharpDepth = Math.Max(0, csharpDepth + openings - closures);
                if (closures > 0)
                {
                    caseIndent = 0;
                }

                if (caseLabel)
                {
                    caseIndent = 1;
                }
            }
        }

        string formatted = builder.ToString();
        return string.Equals(source, formatted, StringComparison.Ordinal)
            ? text
            : SourceText.From(formatted, text.Encoding, text.ChecksumAlgorithm);
    }

    private static void AnalyzeMarkup(
        string line,
        int indentationLevel,
        FormattingOptions options,
        ref RazorMarkupFormattingState state)
    {
        int openingCount = 0;
        int closingCount = 0;
        int index = 0;
        if (state.InTag)
        {
            int continuedTagEnd = FindTagEnd(line, 0);
            if (continuedTagEnd < 0)
            {
                return;
            }

            state.InTag = false;
            if (state.PendingRawTextElement is not null &&
                !ContainsEndTag(line[(continuedTagEnd + 1)..], state.PendingRawTextElement))
            {
                state.RawTextElement = state.PendingRawTextElement;
            }

            state.PendingRawTextElement = null;
            index = continuedTagEnd + 1;
        }

        while (index < line.Length)
        {
            int tagStart = line.IndexOf('<', index);
            if (tagStart < 0)
            {
                break;
            }

            if (tagStart + 1 >= line.Length ||
                line.AsSpan(tagStart).StartsWith("<!--", StringComparison.Ordinal) ||
                line[tagStart + 1] is '!' or '?')
            {
                index = tagStart + 1;
                continue;
            }

            bool closing = line[tagStart + 1] == '/';
            int nameStart = tagStart + (closing ? 2 : 1);
            int nameEnd = nameStart;
            while (nameEnd < line.Length && IsTagNameCharacter(line[nameEnd]))
            {
                nameEnd++;
            }

            if (nameEnd == nameStart)
            {
                index = tagStart + 1;
                continue;
            }

            string name = line[nameStart..nameEnd];
            int tagEnd = FindTagEnd(line, nameEnd);
            if (tagEnd < 0)
            {
                if (!closing)
                {
                    state.InTag = true;
                    int visualIndent = GetIndentationWidth(indentationLevel, options);
                    state.ContinuationColumn = visualIndent + nameEnd + 1;
                    if (!IsVoidElement(name) &&
                        !name.Equals("html", StringComparison.OrdinalIgnoreCase))
                    {
                        openingCount++;
                        state.PendingRawTextElement = IsRawTextElement(name) ? name : null;
                    }
                }

                break;
            }

            state.InTag = false;
            bool selfClosing = IsSelfClosing(line, tagEnd);
            if (closing)
            {
                closingCount++;
            }
            else if (!selfClosing &&
                !IsVoidElement(name) &&
                !name.Equals("html", StringComparison.OrdinalIgnoreCase))
            {
                openingCount++;
                if (IsRawTextElement(name) &&
                    !ContainsEndTag(line[(tagEnd + 1)..], name))
                {
                    state.RawTextElement = name;
                }
            }

            index = tagEnd + 1;
        }

        state.HtmlDepth = Math.Max(0, state.HtmlDepth + openingCount - closingCount);
    }

    private static (int Openings, int Closures) CountCSharpBraces(string line)
    {
        string csharp = line.StartsWith('@') ? line[1..] : line;
        if (csharp.StartsWith("code", StringComparison.Ordinal) ||
            csharp.StartsWith("functions", StringComparison.Ordinal) ||
            csharp.StartsWith("section", StringComparison.Ordinal))
        {
            int brace = csharp.IndexOf('{', StringComparison.Ordinal);
            csharp = brace < 0 ? string.Empty : csharp[brace..];
        }

        int openings = 0;
        int closures = 0;
        foreach (SyntaxToken token in SyntaxFactory.ParseTokens(csharp))
        {
            if (token.IsKind(SyntaxKind.OpenBraceToken))
            {
                openings++;
            }
            else if (token.IsKind(SyntaxKind.CloseBraceToken))
            {
                closures++;
            }
        }

        return (openings, closures);
    }

    private static int CountLeadingClosures(string content)
    {
        int count = 0;
        while (count < content.Length && content[count] == '}')
        {
            count++;
        }

        return count;
    }

    private static string CreateAlignmentIndentation(
        int visualWidth,
        FormattingOptions options)
    {
        if (options.InsertSpaces)
        {
            return new string(' ', visualWidth);
        }

        int tabs = visualWidth / options.TabSize;
        int spaces = visualWidth % options.TabSize;
        return new string('\t', tabs) + new string(' ', spaces);
    }

    private static string CreateIndentation(int level, FormattingOptions options) =>
        options.InsertSpaces
            ? new string(' ', checked(level * options.TabSize))
            : new string('\t', level);

    private static int FindTagEnd(string line, int start)
    {
        char quote = '\0';
        for (int index = start; index < line.Length; index++)
        {
            char character = line[index];
            if (quote == '\0' && character is '\'' or '"')
            {
                quote = character;
            }
            else if (quote != '\0' && character == quote)
            {
                quote = '\0';
            }
            else if (quote == '\0' && character == '>')
            {
                return index;
            }
        }

        return -1;
    }

    private static string FormatCSharpLine(string content)
    {
        string directiveFormatted = FormatBlockDirective(content);
        if (directiveFormatted.StartsWith("@code", StringComparison.Ordinal) ||
            directiveFormatted.StartsWith("@functions", StringComparison.Ordinal) ||
            directiveFormatted.StartsWith("@section", StringComparison.Ordinal))
        {
            return directiveFormatted;
        }

        if (directiveFormatted is "{" or "}" ||
            directiveFormatted.StartsWith("} else", StringComparison.Ordinal) ||
            directiveFormatted.StartsWith("} catch", StringComparison.Ordinal) ||
            directiveFormatted.StartsWith("} finally", StringComparison.Ordinal) ||
            IsCaseLabel(directiveFormatted))
        {
            return directiveFormatted;
        }

        if (directiveFormatted.Contains("//", StringComparison.Ordinal))
        {
            return AddControlKeywordSpacing(directiveFormatted);
        }

        string transition = string.Empty;
        string csharp = directiveFormatted;
        if (csharp.StartsWith('@') &&
            !csharp.StartsWith("@@", StringComparison.Ordinal))
        {
            transition = "@";
            csharp = csharp[1..];
        }

        if (transition.Length > 0 && csharp.StartsWith('('))
        {
            return transition + FormatParenthesizedExpression(csharp);
        }

        SyntaxNode? syntax = StartsMemberDeclaration(csharp)
            ? SyntaxFactory.ParseMemberDeclaration(csharp)
            : SyntaxFactory.ParseStatement(csharp);
        if (syntax is null || syntax.ContainsSkippedText)
        {
            return AddControlKeywordSpacing(directiveFormatted);
        }

        string normalized = syntax
            .NormalizeWhitespace(indentation: string.Empty, eol: " ", elasticTrivia: false)
            .ToFullString()
            .Trim();
        return normalized.Length == 0
            ? directiveFormatted
            : transition + normalized;
    }

    private static string FormatExplicitExpressions(string content)
    {
        if (!content.Contains("@(", StringComparison.Ordinal))
        {
            return content;
        }

        int searchStart = 0;
        var builder = new StringBuilder(content.Length);
        while (searchStart < content.Length)
        {
            int expressionStart = content.IndexOf("@(", searchStart, StringComparison.Ordinal);
            if (expressionStart < 0)
            {
                builder.Append(content, searchStart, content.Length - searchStart);
                break;
            }

            int expressionEnd = FindBalancedParenthesis(content, expressionStart + 1);
            if (expressionEnd < 0)
            {
                builder.Append(content, searchStart, content.Length - searchStart);
                break;
            }

            builder.Append(content, searchStart, expressionStart - searchStart);
            builder.Append('@');
            builder.Append(FormatParenthesizedExpression(
                content[(expressionStart + 1)..(expressionEnd + 1)]));
            searchStart = expressionEnd + 1;
        }

        return builder.ToString();
    }

    private static string FormatParenthesizedExpression(string value)
    {
        if (value.Length < 2 || value[0] != '(' || value[^1] != ')')
        {
            return value;
        }

        string expressionText = value[1..^1];
        Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax expression =
            SyntaxFactory.ParseExpression(expressionText);
        if (expression.ContainsDiagnostics || expression.ContainsSkippedText)
        {
            return value;
        }

        string formatted = expression
            .NormalizeWhitespace(indentation: string.Empty, eol: " ", elasticTrivia: false)
            .ToFullString();
        return $"({formatted})";
    }

    private static int FindBalancedParenthesis(string content, int openIndex)
    {
        int depth = 0;
        bool escaped = false;
        char quote = '\0';
        for (int index = openIndex; index < content.Length; index++)
        {
            char character = content[index];
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '(')
            {
                depth++;
            }
            else if (character == ')' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static string FormatBlockDirective(string content)
    {
        if (!content.StartsWith("@code", StringComparison.Ordinal) &&
            !content.StartsWith("@functions", StringComparison.Ordinal) &&
            !content.StartsWith("@section", StringComparison.Ordinal))
        {
            return content;
        }

        int brace = content.IndexOf('{', StringComparison.Ordinal);
        if (brace <= 0 || char.IsWhiteSpace(content[brace - 1]))
        {
            return content;
        }

        return content.Insert(brace, " ");
    }

    private static string AddControlKeywordSpacing(string content)
    {
        int transitionOffset = content.StartsWith('@') ? 1 : 0;
        for (int index = 0; index < s_controlKeywords.Length; index++)
        {
            string keyword = s_controlKeywords[index];
            if (content.AsSpan(transitionOffset).StartsWith(keyword, StringComparison.Ordinal) &&
                content.Length > transitionOffset + keyword.Length &&
                content[transitionOffset + keyword.Length] == '(')
            {
                return content.Insert(transitionOffset + keyword.Length, " ");
            }
        }

        return content;
    }

    private static bool StartsMemberDeclaration(string content) =>
        content.StartsWith("public ", StringComparison.Ordinal) ||
        content.StartsWith("internal ", StringComparison.Ordinal) ||
        content.StartsWith("protected ", StringComparison.Ordinal) ||
        content.StartsWith("private ", StringComparison.Ordinal) ||
        content.StartsWith("static ", StringComparison.Ordinal) ||
        content.StartsWith("sealed ", StringComparison.Ordinal) ||
        content.StartsWith("abstract ", StringComparison.Ordinal) ||
        content.StartsWith("partial ", StringComparison.Ordinal) ||
        content.StartsWith("readonly ", StringComparison.Ordinal);

    private static bool IsCSharpLine(string content, int csharpDepth)
    {
        if (content.StartsWith("@*", StringComparison.Ordinal) ||
            content.StartsWith("@page", StringComparison.Ordinal) ||
            content.StartsWith("@using", StringComparison.Ordinal) ||
            content.StartsWith("@namespace", StringComparison.Ordinal) ||
            content.StartsWith("@inject", StringComparison.Ordinal) ||
            content.StartsWith("@inherits", StringComparison.Ordinal) ||
            content.StartsWith("@layout", StringComparison.Ordinal) ||
            content.StartsWith("@attribute", StringComparison.Ordinal) ||
            content.StartsWith("@typeparam", StringComparison.Ordinal))
        {
            return false;
        }

        if (content.StartsWith('<'))
        {
            return false;
        }

        return csharpDepth > 0 ||
            content.StartsWith('@') ||
            content.StartsWith('{') ||
            content.StartsWith('}') ||
            content.StartsWith("case ", StringComparison.Ordinal) ||
            content.StartsWith("default:", StringComparison.Ordinal) ||
            content.StartsWith("else", StringComparison.Ordinal) ||
            content.StartsWith("catch", StringComparison.Ordinal) ||
            content.StartsWith("finally", StringComparison.Ordinal);
    }

    private static bool IsCaseLabel(string content) =>
        content.StartsWith("case ", StringComparison.Ordinal) ||
        content.StartsWith("default:", StringComparison.Ordinal);

    private static bool IsSelfClosing(string line, int tagEnd)
    {
        int index = tagEnd - 1;
        while (index >= 0 && char.IsWhiteSpace(line[index]))
        {
            index--;
        }

        return index >= 0 && line[index] == '/';
    }

    private static bool IsRawTextElement(string name) =>
        ContainsElement(s_rawTextElements, name);

    private static bool IsVoidElement(string name) =>
        ContainsElement(s_voidElements, name);

    private static bool ContainsElement(string[] elements, string name)
    {
        for (int index = 0; index < elements.Length; index++)
        {
            if (elements[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTagNameCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.';

    private static bool ContainsEndTag(string line, string name) =>
        line.Contains($"</{name}", StringComparison.OrdinalIgnoreCase);

    private static int GetIndentationWidth(int level, FormattingOptions options) =>
        checked(level * options.TabSize);

    private static void AppendLineBreak(
        StringBuilder builder,
        string source,
        TextLine line) =>
        builder.Append(source, line.End, line.EndIncludingLineBreak - line.End);

}
