using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using LspCodeAction = Csls.Protocol.CodeAction;
using RoslynCodeAction = Microsoft.CodeAnalysis.CodeActions.CodeAction;

namespace Csls.Workspaces;

/// <summary>
/// Adapts Roslyn's complete exported code-refactoring pipeline to concrete LSP edits.
/// </summary>
internal static class WorkspaceRoslynCodeRefactoringService
{
    private const string RefactorCodeActionKind = "refactor";
    private const string IntroduceVariableProviderTypeName =
        "Microsoft.CodeAnalysis.IntroduceVariable.IntroduceVariableCodeRefactoringProvider";

    private static readonly IProgress<CodeAnalysisProgress> s_progress =
        new Progress<CodeAnalysisProgress>(static _ => { });

    private static readonly Lazy<RoslynCodeRefactoringProviderCatalog> s_providerCatalog =
        new(
            RoslynCodeRefactoringProviderCatalog.Create,
            LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Gets the Roslyn code refactorings for one document range.
    /// </summary>
    internal static async Task<IReadOnlyList<LspCodeAction>> GetActionsAsync(
        Document document,
        CodeActionParams parameters,
        bool supportsCreateFile,
        Func<Solution, Solution, CancellationToken, Task<WorkspaceEdit>>
            createWorkspaceEditAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(createWorkspaceEditAsync);

        SourceText text = await document.GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        int start = LspPositionConverter.GetOffset(text, parameters.Range.Start);
        int end = LspPositionConverter.GetOffset(text, parameters.Range.End);
        var span = TextSpan.FromBounds(start, Math.Max(start, end));
        var roslynActions = new List<RoslynCodeAction>();
        bool isConditionalAccessBindingPosition =
            await IsConditionalAccessBindingPositionAsync(
                document,
                span,
                cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CodeRefactoringProvider> providers = s_providerCatalog.Value.GetProviders(
            document.Project.Solution.Workspace.Services.HostServices,
            document.Project.Language);
        foreach (CodeRefactoringProvider provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isConditionalAccessBindingPosition && string.Equals(
                provider.GetType().FullName,
                IntroduceVariableProviderTypeName,
                StringComparison.Ordinal))
            {
                continue;
            }

            var context = new CodeRefactoringContext(
                document,
                span,
                roslynActions.Add,
                cancellationToken);
            await provider.ComputeRefactoringsAsync(context).ConfigureAwait(false);
        }

        Solution originalSolution = document.Project.Solution;
        var actions = new List<LspCodeAction>();
        var identities = new HashSet<(string Title, string? EquivalenceKey)>();
        foreach (RoslynCodeAction roslynAction in roslynActions.SelectMany(GetLeafActions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (roslynAction is CodeActionWithOptions actionWithOptions &&
                !RoslynCodeActionOptionsContract.IsOptionServiceAvailable(actionWithOptions))
            {
                continue;
            }

            Solution? changedSolution = await GetChangedSolutionAsync(
                roslynAction,
                originalSolution,
                cancellationToken).ConfigureAwait(false);
            if (changedSolution is null)
            {
                continue;
            }

            WorkspaceEdit edit = await createWorkspaceEditAsync(
                originalSolution,
                changedSolution,
                cancellationToken).ConfigureAwait(false);
            if (edit.DocumentChanges.Count == 0 ||
                (!supportsCreateFile && edit.DocumentChanges.Any(
                    static change => change is CreateFile)) ||
                !identities.Add((roslynAction.Title, roslynAction.EquivalenceKey)))
            {
                continue;
            }

            actions.Add(new LspCodeAction
            {
                Title = roslynAction.Title,
                Kind = RefactorCodeActionKind,
                Edit = edit
            });
        }

        if (!actions.Any(static action => action.Title.StartsWith(
            "Extract base class",
            StringComparison.Ordinal)))
        {
            LspCodeAction? extractBaseClassAction =
                await RoslynExtractBaseClassCodeRefactoringAdapter.GetActionAsync(
                    document,
                    span,
                    createWorkspaceEditAsync,
                    cancellationToken).ConfigureAwait(false);
            if (extractBaseClassAction is not null)
            {
                actions.Add(extractBaseClassAction);
            }
        }

        return actions;
    }

    private static async Task<bool> IsConditionalAccessBindingPositionAsync(
        Document document,
        TextSpan span,
        CancellationToken cancellationToken)
    {
        if (document.Project.Language != LanguageNames.CSharp)
        {
            return false;
        }

        SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false);
        if (root is null || root.FullSpan.IsEmpty)
        {
            return false;
        }

        int position = Math.Min(span.Start, root.FullSpan.End - 1);
        return root.FindToken(position, findInsideTrivia: true)
            .Parent?
            .AncestorsAndSelf()
            .Any(static node =>
                node.Parent is ConditionalAccessExpressionSyntax conditionalAccess &&
                conditionalAccess.WhenNotNull == node) == true;
    }

    private static IEnumerable<RoslynCodeAction> GetLeafActions(
        RoslynCodeAction action)
    {
        if (action.NestedActions.IsEmpty)
        {
            yield return action;
            yield break;
        }

        foreach (RoslynCodeAction nestedAction in action.NestedActions)
        {
            foreach (RoslynCodeAction leafAction in GetLeafActions(nestedAction))
            {
                yield return leafAction;
            }
        }
    }

    private static async Task<Solution?> GetChangedSolutionAsync(
        RoslynCodeAction action,
        Solution originalSolution,
        CancellationToken cancellationToken)
    {
        ImmutableArray<CodeActionOperation> operations = await action.GetOperationsAsync(
            originalSolution,
            s_progress,
            cancellationToken).ConfigureAwait(false);
        ApplyChangesOperation[] applyChanges =
            [.. operations.OfType<ApplyChangesOperation>()];
        return applyChanges.Length == 1
            ? applyChanges[0].ChangedSolution
            : null;
    }
}
