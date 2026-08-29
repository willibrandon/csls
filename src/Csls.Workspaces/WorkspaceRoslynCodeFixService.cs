using System.Collections.Immutable;
using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using LspCodeAction = Csls.Protocol.CodeAction;
using LspDiagnostic = Csls.Protocol.Diagnostic;
using RoslynCodeAction = Microsoft.CodeAnalysis.CodeActions.CodeAction;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace Csls.Workspaces;

/// <summary>
/// Adapts Roslyn's complete exported code-fix provider pipeline to concrete LSP edits.
/// </summary>
internal static class WorkspaceRoslynCodeFixService
{
    private const string QuickFixCodeActionKind = "quickfix";
    private const string RequiresNonDocumentChangeTag = "RequiresNonDocumentChange";

    private static readonly IProgress<CodeAnalysisProgress> s_progress =
        new Progress<CodeAnalysisProgress>(static _ => { });

    private static readonly Lazy<RoslynCodeFixProviderCatalog> s_providerCatalog =
        new(
            RoslynCodeFixProviderCatalog.Create,
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<RoslynDiagnosticAnalyzerCatalog> s_analyzerCatalog =
        new(
            RoslynDiagnosticAnalyzerCatalog.Create,
            LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Gets the Roslyn code fixes for one document range.
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
        Solution originalSolution = document.Project.Solution;
        IReadOnlyList<RoslynDiagnostic> roslynDiagnostics = await GetDiagnosticsAsync(
            document,
            span,
            parameters.Context.Diagnostics,
            cancellationToken).ConfigureAwait(false);
        if (roslynDiagnostics.Count == 0)
        {
            return [];
        }

        IReadOnlyList<RoslynCodeFix> fixes = await GetFixesAsync(
            document,
            roslynDiagnostics,
            cancellationToken).ConfigureAwait(false);

        var actions = new List<LspCodeAction>();
        var identities = new HashSet<(string Title, string? EquivalenceKey)>();
        foreach (RoslynCodeFix fix in fixes)
        {
            foreach (RoslynCodeAction roslynAction in GetLeafActions(fix.Action))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (roslynAction.Tags.Contains(RequiresNonDocumentChangeTag))
                {
                    continue;
                }

                if (roslynAction is CodeActionWithOptions actionWithOptions &&
                    !RoslynCodeActionOptionsContract.IsOptionServiceAvailable(
                        actionWithOptions))
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

                IReadOnlyList<LspDiagnostic>? diagnostics = GetClientDiagnostics(
                    parameters.Context.Diagnostics,
                    fix.Diagnostics);
                actions.Add(new LspCodeAction
                {
                    Title = roslynAction.Title,
                    Kind = QuickFixCodeActionKind,
                    Diagnostics = diagnostics,
                    Edit = edit
                });
            }
        }

        return actions;
    }

    private static async Task<IReadOnlyList<RoslynDiagnostic>> GetDiagnosticsAsync(
        Document document,
        TextSpan requestedSpan,
        IReadOnlyList<LspDiagnostic> clientDiagnostics,
        CancellationToken cancellationToken)
    {
        SemanticModel semanticModel = await document
            .GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Roslyn returned no semantic model for {document.Name}.");
        SyntaxTree syntaxTree = semanticModel.SyntaxTree;
        var requestedIds = clientDiagnostics
            .Where(static diagnostic => diagnostic.Code is not null)
            .Select(static diagnostic => diagnostic.Code!)
            .ToHashSet(StringComparer.Ordinal);
        var diagnostics = new List<RoslynDiagnostic>();
        diagnostics.AddRange(semanticModel.GetDiagnostics(
            requestedSpan,
            cancellationToken).Where(diagnostic =>
                IsApplicableDiagnostic(
                    diagnostic,
                    syntaxTree,
                    requestedSpan,
                    requestedIds)));

        ImmutableArray<DiagnosticAnalyzer> analyzers =
        [
            .. document.Project.AnalyzerReferences
                .SelectMany(reference =>
                    reference.GetAnalyzers(document.Project.Language))
                .Concat(s_analyzerCatalog.Value.GetAnalyzers(
                    document.Project.Language))
                .DistinctBy(static analyzer => analyzer.GetType().FullName)
                .Where(analyzer =>
                    requestedIds.Count == 0 ||
                    analyzer.SupportedDiagnostics.Any(descriptor =>
                        requestedIds.Contains(descriptor.Id)))
        ];
        if (!analyzers.IsDefaultOrEmpty)
        {
            CompilationWithAnalyzers compilationWithAnalyzers =
                semanticModel.Compilation.WithAnalyzers(
                    analyzers,
                    document.Project.AnalyzerOptions);
            ImmutableArray<RoslynDiagnostic> analyzerDiagnostics =
                await compilationWithAnalyzers
                    .GetAnalyzerDiagnosticsAsync(cancellationToken)
                    .ConfigureAwait(false);
            diagnostics.AddRange(analyzerDiagnostics.Where(diagnostic =>
                IsApplicableDiagnostic(
                    diagnostic,
                    syntaxTree,
                    requestedSpan,
                    requestedIds)));
        }

        return
        [
            .. diagnostics
                .DistinctBy(static diagnostic =>
                    (diagnostic.Id, diagnostic.Location.SourceSpan))
                .OrderBy(static diagnostic => diagnostic.Location.SourceSpan.Start)
                .ThenBy(static diagnostic => diagnostic.Id, StringComparer.Ordinal)
        ];
    }

    private static bool IsApplicableDiagnostic(
        RoslynDiagnostic diagnostic,
        SyntaxTree syntaxTree,
        TextSpan requestedSpan,
        HashSet<string> requestedIds)
    {
        if (diagnostic.IsSuppressed ||
            !diagnostic.Location.IsInSource ||
            diagnostic.Location.SourceTree != syntaxTree ||
            (requestedIds.Count != 0 && !requestedIds.Contains(diagnostic.Id)))
        {
            return false;
        }

        TextSpan diagnosticSpan = diagnostic.Location.SourceSpan;
        return requestedSpan.IsEmpty
            ? diagnosticSpan.Contains(requestedSpan.Start)
            : diagnosticSpan.IntersectsWith(requestedSpan);
    }

    private static async Task<IReadOnlyList<RoslynCodeFix>> GetFixesAsync(
        Document document,
        IReadOnlyList<RoslynDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CodeFixProvider> providers = s_providerCatalog.Value.GetProviders(
            document.Project.Solution.Workspace.Services.HostServices,
            document.Project.Language);
        var fixes = new List<RoslynCodeFix>();
        foreach (CodeFixProvider provider in providers)
        {
            var fixableIds = provider.FixableDiagnosticIds.ToHashSet(StringComparer.Ordinal);
            foreach (IGrouping<TextSpan, RoslynDiagnostic> diagnosticGroup in diagnostics
                .Where(diagnostic => fixableIds.Contains(diagnostic.Id))
                .GroupBy(static diagnostic => diagnostic.Location.SourceSpan))
            {
                ImmutableArray<RoslynDiagnostic> fixableDiagnostics =
                    [.. diagnosticGroup];
                var context = new CodeFixContext(
                    document,
                    diagnosticGroup.Key,
                    fixableDiagnostics,
                    (action, fixedDiagnostics) => fixes.Add(
                        new RoslynCodeFix(action, fixedDiagnostics)),
                    cancellationToken);
                await provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);
            }
        }

        return fixes;
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

    private static LspDiagnostic[]? GetClientDiagnostics(
        IReadOnlyList<LspDiagnostic> clientDiagnostics,
        IReadOnlyList<RoslynDiagnostic> roslynDiagnostics)
    {
        if (clientDiagnostics.Count == 0 || roslynDiagnostics.Count == 0)
        {
            return null;
        }

        var diagnosticIds = roslynDiagnostics
            .Select(static diagnostic => diagnostic.Id)
            .ToHashSet(StringComparer.Ordinal);
        LspDiagnostic[] matchingDiagnostics =
        [
            .. clientDiagnostics.Where(diagnostic =>
                diagnostic.Code is not null &&
                diagnosticIds.Contains(diagnostic.Code))
        ];
        return matchingDiagnostics.Length == 0 ? null : matchingDiagnostics;
    }
}
