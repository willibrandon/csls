using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using LspCodeAction = Csls.Protocol.CodeAction;
using LspRange = Csls.Protocol.Range;

namespace Csls.Workspaces;

/// <summary>
/// Produces a semantic quick fix for a C# name that Roslyn can safely simplify.
/// </summary>
internal static class WorkspaceSimplifyNameCodeActionService
{
    /// <summary>
    /// Gets a concrete quick fix for the smallest qualified name covering the requested range.
    /// </summary>
    /// <param name="document">The current Roslyn document.</param>
    /// <param name="parameters">The target range and client action context.</param>
    /// <param name="createWorkspaceEditAsync">Creates a version-aware LSP workspace edit.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The semantic simplification, or an empty collection when none is valid.</returns>
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
        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The simplify-name document has no syntax root.");
        TextSpan requestedSpan = ToTextSpan(text, parameters.Range);
        SyntaxNode? qualifiedName = FindQualifiedName(root, requestedSpan);
        if (qualifiedName is null)
        {
            return [];
        }

        string originalName = qualifiedName.WithoutTrivia().ToFullString();
        var targetAnnotation = new SyntaxAnnotation();
        SyntaxNode annotatedName = qualifiedName.WithAdditionalAnnotations(
            targetAnnotation,
            Simplifier.Annotation);
        Document annotatedDocument = document.WithSyntaxRoot(
            root.ReplaceNode(qualifiedName, annotatedName));
        Document simplifiedDocument = await Simplifier.ReduceAsync(
            annotatedDocument,
            targetAnnotation,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        SourceText simplifiedText = await simplifiedDocument.GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        if (text.ContentEquals(simplifiedText))
        {
            return [];
        }

        WorkspaceEdit edit = await createWorkspaceEditAsync(
            document.Project.Solution,
            simplifiedDocument.Project.Solution,
            cancellationToken).ConfigureAwait(false);
        if (edit.DocumentChanges.Count == 0)
        {
            return [];
        }

        return
        [
            new LspCodeAction
            {
                Title = $"Simplify name '{originalName}'",
                Kind = "quickfix",
                IsPreferred = true,
                Edit = edit
            }
        ];
    }

    private static SyntaxNode? FindQualifiedName(SyntaxNode root, TextSpan requestedSpan)
    {
        int position = Math.Min(requestedSpan.Start, root.FullSpan.End - 1);
        return root
            .FindToken(position, findInsideTrivia: true)
            .Parent?
            .AncestorsAndSelf()
            .Where(static node => node is MemberAccessExpressionSyntax or
                QualifiedNameSyntax or AliasQualifiedNameSyntax)
            .Where(node => requestedSpan.IsEmpty
                ? node.Span.Contains(requestedSpan.Start)
                : node.Span.Contains(requestedSpan))
            .OrderBy(static node => node.Span.Length)
            .FirstOrDefault();
    }

    private static TextSpan ToTextSpan(SourceText text, LspRange range)
    {
        int start = LspPositionConverter.GetOffset(text, range.Start);
        int end = LspPositionConverter.GetOffset(text, range.End);
        return TextSpan.FromBounds(start, Math.Max(start, end));
    }
}
