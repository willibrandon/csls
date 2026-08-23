using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using LspRange = Csls.Protocol.Range;
using LspTextEdit = Csls.Protocol.TextEdit;

namespace Csls.Workspaces;

/// <summary>
/// Converts generated C# completion imports into Razor source directives.
/// </summary>
internal static class WorkspaceRazorCompletionEditService
{
    /// <summary>
    /// Creates a Razor <c>@using</c> insertion from one generated C# completion change.
    /// </summary>
    /// <param name="mappedDocument">The generated and Razor document context.</param>
    /// <param name="change">The generated C# completion change.</param>
    /// <param name="edit">The mapped Razor source edit when successful.</param>
    /// <returns>True when the change contains only supported C# using directives.</returns>
    internal static bool TryCreateUsingEdit(
        RazorMappedDocument mappedDocument,
        TextChange change,
        out LspTextEdit edit)
    {
        ArgumentNullException.ThrowIfNull(mappedDocument);
        string? newText = change.NewText;
        if (string.IsNullOrWhiteSpace(newText) ||
            !newText.Contains("using ", StringComparison.Ordinal))
        {
            edit = null!;
            return false;
        }

        CompilationUnitSyntax compilationUnit = SyntaxFactory.ParseCompilationUnit(newText);
        if (compilationUnit.ContainsDiagnostics ||
            compilationUnit.Usings.Count == 0 ||
            compilationUnit.AttributeLists.Count != 0 ||
            compilationUnit.Members.Count != 0)
        {
            edit = null!;
            return false;
        }

        var directives = new List<string>(compilationUnit.Usings.Count);
        for (int index = 0; index < compilationUnit.Usings.Count; index++)
        {
            UsingDirectiveSyntax usingDirective = compilationUnit.Usings[index];
            string? directive = CreateDirective(usingDirective);
            if (directive is null)
            {
                edit = null!;
                return false;
            }

            directives.Add(directive);
        }

        SourceText razorText = mappedDocument.RazorText;
        int insertionOffset = GetInsertionOffset(razorText);
        LinePosition insertionPosition = razorText.Lines.GetLinePosition(insertionOffset);
        string newLine = GetNewLine(razorText);
        string prefix = insertionOffset > 0 &&
            razorText[insertionOffset - 1] is not ('\r' or '\n')
            ? newLine
            : string.Empty;
        edit = new LspTextEdit
        {
            Range = new LspRange(
                new Position(insertionPosition.Line, insertionPosition.Character),
                new Position(insertionPosition.Line, insertionPosition.Character)),
            NewText = string.Concat(
                prefix,
                string.Join(newLine, directives),
                newLine)
        };
        return true;
    }

    private static string? CreateDirective(UsingDirectiveSyntax usingDirective)
    {
        NameSyntax? name = usingDirective.Name;
        if (!usingDirective.GlobalKeyword.IsKind(SyntaxKind.None) ||
            name is null)
        {
            return null;
        }

        string target = name.ToString();
        if (usingDirective.Alias is NameEqualsSyntax alias)
        {
            return $"@using {alias.Name.Identifier.ValueText} = {target}";
        }

        return usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)
            ? $"@using static {target}"
            : $"@using {target}";
    }

    private static int GetInsertionOffset(SourceText text)
    {
        int insertionOffset = 0;
        foreach (TextLine line in text.Lines)
        {
            ReadOnlySpan<char> content = text.ToString(line.Span).AsSpan().TrimStart();
            if (IsDirective(content, "@page") ||
                IsDirective(content, "@namespace") ||
                IsDirective(content, "@using"))
            {
                insertionOffset = line.EndIncludingLineBreak;
                continue;
            }

            break;
        }

        return insertionOffset;
    }

    private static bool IsDirective(ReadOnlySpan<char> content, string directive) =>
        content.StartsWith(directive, StringComparison.Ordinal) &&
        (content.Length == directive.Length || char.IsWhiteSpace(content[directive.Length]));

    private static string GetNewLine(SourceText text)
    {
        for (int index = 0; index < text.Lines.Count; index++)
        {
            TextLine line = text.Lines[index];
            if (line.EndIncludingLineBreak > line.End)
            {
                return text.ToString(TextSpan.FromBounds(line.End, line.EndIncludingLineBreak));
            }
        }

        return Environment.NewLine;
    }
}
