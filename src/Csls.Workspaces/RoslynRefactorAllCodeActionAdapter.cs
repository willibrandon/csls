using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Reflection;
using LspCodeAction = Csls.Protocol.CodeAction;
using RoslynCodeAction = Microsoft.CodeAnalysis.CodeActions.CodeAction;

namespace Csls.Workspaces;

/// <summary>
/// Adapts Roslyn refactor-all providers to concrete document-scoped LSP edits.
/// </summary>
internal static class RoslynRefactorAllCodeActionAdapter
{
    private const string RefactorCodeActionKind = "refactor";

    private static readonly IProgress<CodeAnalysisProgress> s_progress =
        new Progress<CodeAnalysisProgress>(static _ => { });

    private static readonly Lazy<(
        ConstructorInfo StateConstructor,
        ConstructorInfo ContextConstructor)> s_contract =
        new(CreateContract, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Gets a document-scoped Fix All action when the originating provider supports it.
    /// </summary>
    internal static async Task<LspCodeAction?> GetActionAsync(
        CodeRefactoringProvider provider,
        RoslynCodeAction action,
        Document document,
        TextSpan selectionSpan,
        bool supportsCreateFile,
        Func<Solution, Solution, CancellationToken, Task<WorkspaceEdit>>
            createWorkspaceEditAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(createWorkspaceEditAsync);
        RefactorAllProvider? refactorAllProvider = provider.GetRefactorAllProvider();
        if (action.EquivalenceKey is null ||
            refactorAllProvider is null ||
            !refactorAllProvider.GetSupportedRefactorAllScopes().Contains(
                RefactorAllScope.Document))
        {
            return null;
        }

        (ConstructorInfo stateConstructor, ConstructorInfo contextConstructor) =
            s_contract.Value;
        object state = stateConstructor.Invoke(
        [
            refactorAllProvider,
            document,
            selectionSpan,
            provider,
            RefactorAllScope.Document,
            action
        ]);
        RefactorAllContext context = contextConstructor.Invoke(
            [state, s_progress, cancellationToken]) as RefactorAllContext
            ?? throw new InvalidOperationException(
                "Roslyn's refactor-all context could not be created.");
        RoslynCodeAction? refactorAllAction = await refactorAllProvider
            .GetRefactoringAsync(context).ConfigureAwait(false);
        if (refactorAllAction is null)
        {
            return null;
        }

        ImmutableArray<CodeActionOperation> operations =
            await refactorAllAction.GetOperationsAsync(
                document.Project.Solution,
                s_progress,
                cancellationToken).ConfigureAwait(false);
        ApplyChangesOperation[] applyChanges =
            [.. operations.OfType<ApplyChangesOperation>()];
        if (applyChanges.Length != 1)
        {
            return null;
        }

        WorkspaceEdit edit = await createWorkspaceEditAsync(
            document.Project.Solution,
            applyChanges[0].ChangedSolution,
            cancellationToken).ConfigureAwait(false);
        return edit.DocumentChanges.Count == 0 ||
            (!supportsCreateFile && edit.DocumentChanges.Any(
                static change => change is CreateFile))
            ? null
            : new LspCodeAction
            {
                Title = $"Fix All: {action.Title}",
                Kind = RefactorCodeActionKind,
                Edit = edit
            };
    }

    private static (
        ConstructorInfo StateConstructor,
        ConstructorInfo ContextConstructor) CreateContract()
    {
        Assembly assembly = typeof(RefactorAllContext).Assembly;
        Type stateType = assembly.GetType(
            "Microsoft.CodeAnalysis.CodeRefactorings.RefactorAllState",
            throwOnError: true)
            ?? throw new InvalidOperationException(
                "Roslyn's refactor-all state type was not found.");
        ConstructorInfo stateConstructor = stateType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            [
                typeof(RefactorAllProvider),
                typeof(Document),
                typeof(TextSpan),
                typeof(CodeRefactoringProvider),
                typeof(RefactorAllScope),
                typeof(RoslynCodeAction)
            ]) ?? throw new InvalidOperationException(
                "Roslyn's document refactor-all state constructor was not found.");
        ConstructorInfo contextConstructor = typeof(RefactorAllContext).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            [stateType, typeof(IProgress<CodeAnalysisProgress>), typeof(CancellationToken)])
            ?? throw new InvalidOperationException(
                "Roslyn's refactor-all context constructor was not found.");
        return (stateConstructor, contextConstructor);
    }
}
