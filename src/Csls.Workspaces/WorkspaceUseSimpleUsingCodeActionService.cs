using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using LspCodeAction = Csls.Protocol.CodeAction;
using LspDiagnostic = Csls.Protocol.Diagnostic;

namespace Csls.Workspaces;

/// <summary>
/// Produces the IDE0063 quick fix from a real Roslyn syntax tree.
/// </summary>
internal static class WorkspaceUseSimpleUsingCodeActionService
{
    private const string DiagnosticId = "IDE0063";

    /// <summary>
    /// Gets a concrete quick fix for a matching simple-using diagnostic.
    /// </summary>
    /// <param name="document">The current Roslyn document.</param>
    /// <param name="parameters">The target range and client diagnostic context.</param>
    /// <param name="createWorkspaceEditAsync">Creates a version-aware LSP workspace edit.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The matching quick fix, or an empty collection when it is not applicable.</returns>
    internal static async Task<IReadOnlyList<LspCodeAction>> GetActionsAsync(
        Document document,
        CodeActionParams parameters,
        Func<Solution, Solution, CancellationToken, Task<WorkspaceEdit>>
            createWorkspaceEditAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(createWorkspaceEditAsync);
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        LspDiagnostic? diagnostic = parameters.Context.Diagnostics.FirstOrDefault(
            diagnostic =>
                string.Equals(diagnostic.Code, DiagnosticId, StringComparison.Ordinal) &&
                ToTextSpan(text, diagnostic.Range).IntersectsWith(
                    ToTextSpan(text, parameters.Range)));
        if (diagnostic is null)
        {
            return [];
        }

        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The code-action document has no syntax root.");
        int diagnosticStart = LspPositionConverter.GetOffset(text, diagnostic.Range.Start);
        UsingStatementSyntax? usingStatement = root
            .FindToken(diagnosticStart, findInsideTrivia: true)
            .Parent?
            .AncestorsAndSelf()
            .OfType<UsingStatementSyntax>()
            .FirstOrDefault();
        if (usingStatement?.Declaration is null)
        {
            return [];
        }

        IReadOnlyList<StatementSyntax> expanded = Expand(usingStatement);
        SyntaxNode? changedRoot = ReplaceUsingStatement(root, usingStatement, expanded);
        if (changedRoot is null)
        {
            return [];
        }

        Document changedDocument = document.WithSyntaxRoot(changedRoot);
        changedDocument = await Formatter.FormatAsync(
            changedDocument,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        WorkspaceEdit edit = await createWorkspaceEditAsync(
            document.Project.Solution,
            changedDocument.Project.Solution,
            cancellationToken).ConfigureAwait(false);
        if (edit.DocumentChanges.Count == 0)
        {
            return [];
        }

        return
        [
            new LspCodeAction
            {
                Title = "Use simple 'using' statement",
                Kind = "quickfix",
                Diagnostics = [diagnostic],
                IsPreferred = true,
                Edit = edit
            }
        ];
    }

    private static SyntaxNode? ReplaceUsingStatement(
        SyntaxNode root,
        UsingStatementSyntax usingStatement,
        IReadOnlyList<StatementSyntax> expanded)
    {
        if (usingStatement.Parent is BlockSyntax block)
        {
            return root.ReplaceNode(
                block,
                block.WithStatements(block.Statements.ReplaceRange(usingStatement, expanded)));
        }

        if (usingStatement.Parent is GlobalStatementSyntax globalStatement &&
            globalStatement.Parent is CompilationUnitSyntax compilationUnit)
        {
            MemberDeclarationSyntax[] members =
            [.. expanded.Select(SyntaxFactory.GlobalStatement)];
            return root.ReplaceNode(
                compilationUnit,
                compilationUnit.WithMembers(
                    compilationUnit.Members.ReplaceRange(globalStatement, members)));
        }

        return null;
    }

    private static List<StatementSyntax> Expand(UsingStatementSyntax usingStatement)
    {
        var statements = new List<StatementSyntax> { Convert(usingStatement) };
        SyntaxTriviaList remainingTrivia = ExpandBody(statements, usingStatement.Statement);
        if (remainingTrivia.Any(IsCommentOrDirective))
        {
            StatementSyntax last = statements[^1];
            statements[^1] = last.WithTrailingTrivia(
                last.GetTrailingTrivia()
                    .Add(SyntaxFactory.ElasticCarriageReturnLineFeed)
                    .AddRange(remainingTrivia));
        }

        for (int index = 0; index < statements.Count; index++)
        {
            statements[index] = statements[index].WithAdditionalAnnotations(Formatter.Annotation);
        }

        return statements;
    }

    private static SyntaxTriviaList ExpandBody(
        List<StatementSyntax> result,
        StatementSyntax statement)
    {
        if (statement is UsingStatementSyntax nestedUsing && nestedUsing.Declaration is not null)
        {
            result.Add(Convert(nestedUsing));
            return ExpandBody(result, nestedUsing.Statement);
        }

        if (statement is not BlockSyntax block)
        {
            result.Add(statement);
            return default;
        }

        SyntaxList<StatementSyntax> statements = block.Statements;
        if (statements.Count == 0)
        {
            return block.CloseBraceToken.LeadingTrivia;
        }

        StatementSyntax first = statements[0];
        if (!block.OpenBraceToken.TrailingTrivia.Any(
                static trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia)))
        {
            first = first.WithLeadingTrivia(
                first.GetLeadingTrivia().Insert(0, SyntaxFactory.ElasticCarriageReturnLineFeed));
        }

        var openBraceTrivia = block.OpenBraceToken.LeadingTrivia
            .AddRange(block.OpenBraceToken.TrailingTrivia)
            .Where(IsCommentOrDirective)
            .ToSyntaxTriviaList();
        if (openBraceTrivia.Count > 0)
        {
            first = first.WithLeadingTrivia(first.GetLeadingTrivia().InsertRange(0, openBraceTrivia));
        }

        statements = statements.Replace(statements[0], first);
        var closeBraceTrivia = block.CloseBraceToken.TrailingTrivia
            .Where(IsCommentOrDirective)
            .ToSyntaxTriviaList();
        if (closeBraceTrivia.Count > 0)
        {
            StatementSyntax last = statements[^1];
            statements = statements.Replace(
                last,
                last.WithTrailingTrivia(last.GetTrailingTrivia().AddRange(closeBraceTrivia)));
        }

        result.AddRange(statements);
        return block.CloseBraceToken.LeadingTrivia;
    }

    private static LocalDeclarationStatementSyntax Convert(UsingStatementSyntax statement) =>
        SyntaxFactory.LocalDeclarationStatement(
                statement.AwaitKeyword,
                statement.UsingKeyword.WithTrailingTrivia(
                    statement.UsingKeyword.TrailingTrivia.Add(SyntaxFactory.ElasticMarker)),
                modifiers: default,
                statement.Declaration!,
                SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(statement.GetLeadingTrivia())
            .WithTrailingTrivia(statement.CloseParenToken.TrailingTrivia);

    private static bool IsCommentOrDirective(SyntaxTrivia trivia) =>
        trivia.IsDirective ||
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
        trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
        trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
        trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);

    private static TextSpan ToTextSpan(SourceText text, Csls.Protocol.Range range)
    {
        int start = LspPositionConverter.GetOffset(text, range.Start);
        int end = LspPositionConverter.GetOffset(text, range.End);
        return TextSpan.FromBounds(start, Math.Max(start, end));
    }
}
