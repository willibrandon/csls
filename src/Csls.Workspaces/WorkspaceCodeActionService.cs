using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using LspCodeAction = Csls.Protocol.CodeAction;
using LspDiagnostic = Csls.Protocol.Diagnostic;
using LspRange = Csls.Protocol.Range;

namespace Csls.Workspaces;

/// <summary>
/// Produces bounded semantic C# code actions from one immutable document snapshot.
/// </summary>
internal static class WorkspaceCodeActionService
{
    private const int MaximumMissingUsingActions = 20;
    private const string QuickFixCodeActionKind = "quickfix";

    private static readonly HashSet<string> s_missingUsingDiagnosticIds =
        new(StringComparer.Ordinal)
        {
            "CS0103",
            "CS0246",
            "CS0305",
            "CS0308",
            "CS0616"
        };

    /// <summary>
    /// Gets verified missing-using quick fixes for the requested source range.
    /// </summary>
    /// <param name="document">The current Roslyn document.</param>
    /// <param name="parameters">The target range and editor diagnostic context.</param>
    /// <param name="createWorkspaceEditAsync">Creates a version-aware LSP workspace edit.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded actions whose imports bind the unresolved source name.</returns>
    internal static async Task<IReadOnlyList<LspCodeAction>> GetMissingUsingActionsAsync(
        Document document,
        CodeActionParams parameters,
        Func<Solution, Solution, CancellationToken, Task<WorkspaceEdit>>
            createWorkspaceEditAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(createWorkspaceEditAsync);

        SourceText sourceText = await document.GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The code-action document has no syntax root.");
        SemanticModel semanticModel = await document.GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The code-action document has no semantic model.");
        SimpleNameSyntax? unresolvedName = FindUnresolvedName(
            root,
            sourceText,
            semanticModel,
            parameters.Range,
            cancellationToken);
        if (unresolvedName is null)
        {
            return [];
        }

        IReadOnlyList<INamedTypeSymbol> candidates = await FindTypeCandidatesAsync(
            document.Project,
            unresolvedName,
            cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return [];
        }

        IReadOnlyList<LspDiagnostic>? diagnostics = GetClientDiagnostics(
            parameters.Context.Diagnostics,
            sourceText,
            unresolvedName.Span);
        var actions = new List<LspCodeAction>(Math.Min(
            candidates.Count,
            MaximumMissingUsingActions));
        foreach (INamedTypeSymbol candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Document? changedDocument = await TryAddUsingAsync(
                document,
                root,
                unresolvedName,
                candidate,
                cancellationToken).ConfigureAwait(false);
            if (changedDocument is null)
            {
                continue;
            }

            WorkspaceEdit edit = await createWorkspaceEditAsync(
                document.Project.Solution,
                changedDocument.Project.Solution,
                cancellationToken).ConfigureAwait(false);
            if (edit.DocumentChanges.Count == 0)
            {
                continue;
            }

            string namespaceName = candidate.ContainingNamespace.ToDisplayString();
            actions.Add(new LspCodeAction
            {
                Title = $"Add using {namespaceName}",
                Kind = QuickFixCodeActionKind,
                Diagnostics = diagnostics,
                IsPreferred = false,
                Edit = edit
            });
            if (actions.Count == MaximumMissingUsingActions)
            {
                break;
            }
        }

        return actions.Count == 1
            ? [actions[0] with { IsPreferred = true }]
            : actions;
    }

    private static SimpleNameSyntax? FindUnresolvedName(
        SyntaxNode root,
        SourceText sourceText,
        SemanticModel semanticModel,
        LspRange range,
        CancellationToken cancellationToken)
    {
        int start = LspPositionConverter.GetOffset(sourceText, range.Start);
        int end = LspPositionConverter.GetOffset(sourceText, range.End);
        var requestedSpan = TextSpan.FromBounds(start, Math.Max(start, end));
        SyntaxToken token = root.FindToken(start, findInsideTrivia: true);
        return token.Parent?
            .AncestorsAndSelf()
            .OfType<SimpleNameSyntax>()
            .Where(name => requestedSpan.IsEmpty
                ? name.Span.Contains(start)
                : name.Span.IntersectsWith(requestedSpan))
            .OrderBy(static name => name.Span.Length)
            .FirstOrDefault(name => semanticModel
                .GetDiagnostics(name.Span, cancellationToken)
                .Any(diagnostic =>
                    s_missingUsingDiagnosticIds.Contains(diagnostic.Id) &&
                    diagnostic.Location.SourceSpan.IntersectsWith(name.Span)));
    }

    private static async Task<IReadOnlyList<INamedTypeSymbol>> FindTypeCandidatesAsync(
        Project project,
        SimpleNameSyntax unresolvedName,
        CancellationToken cancellationToken)
    {
        string identifier = unresolvedName.Identifier.ValueText;
        Compilation compilation = await project.GetCompilationAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The code-action project produced no compilation.");
        string[] lookupNames = unresolvedName.FirstAncestorOrSelf<AttributeSyntax>() is null
            ? [identifier]
            : [identifier, identifier + "Attribute"];
        var candidates = new List<INamedTypeSymbol>();
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (string lookupName in lookupNames)
        {
            IEnumerable<ISymbol> declarations = await SymbolFinder.FindDeclarationsAsync(
                project,
                lookupName,
                ignoreCase: false,
                SymbolFilter.Type,
                cancellationToken).ConfigureAwait(false);
            foreach (INamedTypeSymbol candidate in declarations.OfType<INamedTypeSymbol>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!candidate.CanBeReferencedByName ||
                    candidate.ContainingNamespace is not { IsGlobalNamespace: false } ||
                    !compilation.IsSymbolAccessibleWithin(candidate, compilation.Assembly) ||
                    !MatchesArity(unresolvedName, candidate, lookupName, identifier))
                {
                    continue;
                }

                string namespaceName = candidate.ContainingNamespace.ToDisplayString();
                if (namespaces.Add(namespaceName))
                {
                    candidates.Add(candidate);
                }
            }
        }

        candidates.Sort(static (left, right) => CompareNamespaces(
            left.ContainingNamespace.ToDisplayString(),
            right.ContainingNamespace.ToDisplayString()));
        return candidates;
    }

    private static bool MatchesArity(
        SimpleNameSyntax unresolvedName,
        INamedTypeSymbol candidate,
        string lookupName,
        string identifier) =>
        unresolvedName.Arity == candidate.Arity &&
        (string.Equals(candidate.Name, lookupName, StringComparison.Ordinal) ||
            string.Equals(candidate.Name, identifier + "Attribute", StringComparison.Ordinal));

    private static int CompareNamespaces(string left, string right)
    {
        bool leftSystem = left is "System" || left.StartsWith("System.", StringComparison.Ordinal);
        bool rightSystem = right is "System" || right.StartsWith("System.", StringComparison.Ordinal);
        int systemComparison = rightSystem.CompareTo(leftSystem);
        return systemComparison != 0
            ? systemComparison
            : StringComparer.Ordinal.Compare(left, right);
    }

    private static async Task<Document?> TryAddUsingAsync(
        Document document,
        SyntaxNode root,
        SimpleNameSyntax unresolvedName,
        INamedTypeSymbol candidate,
        CancellationToken cancellationToken)
    {
        string namespaceName = candidate.ContainingNamespace.ToDisplayString();
        string sourceName = unresolvedName.WithoutTrivia().ToFullString();
        NameSyntax qualifiedName = SyntaxFactory.ParseName(
                $"global::{namespaceName}.{sourceName}")
            .WithTriviaFrom(unresolvedName);
        var targetAnnotation = new SyntaxAnnotation();
        qualifiedName = qualifiedName.WithAdditionalAnnotations(
            targetAnnotation,
            Simplifier.Annotation,
            Simplifier.AddImportsAnnotation);
        Document qualifiedDocument = document.WithSyntaxRoot(
            root.ReplaceNode(unresolvedName, qualifiedName));
        Document importedDocument = await ImportAdder.AddImportsAsync(
            qualifiedDocument,
            targetAnnotation,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Document simplifiedDocument = await Simplifier.ReduceAsync(
            importedDocument,
            targetAnnotation,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Document formattedDocument = await Formatter.FormatAsync(
            simplifiedDocument,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!await BindsToCandidateAsync(
                formattedDocument,
                targetAnnotation,
                candidate,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        SourceText originalText = await document.GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        SourceText changedText = await formattedDocument.GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        return originalText.ContentEquals(changedText) ? null : formattedDocument;
    }

    private static async Task<bool> BindsToCandidateAsync(
        Document document,
        SyntaxAnnotation targetAnnotation,
        INamedTypeSymbol candidate,
        CancellationToken cancellationToken)
    {
        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The changed code-action document has no syntax root.");
        SyntaxNode? target = root.GetAnnotatedNodes(targetAnnotation).SingleOrDefault();
        if (target is null)
        {
            return false;
        }

        SemanticModel semanticModel = await document.GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The changed code-action document has no semantic model.");
        ISymbol? symbol = semanticModel.GetSymbolInfo(target, cancellationToken).Symbol;
        return symbol is INamedTypeSymbol boundType &&
            SymbolEqualityComparer.Default.Equals(
                boundType.OriginalDefinition,
                candidate.OriginalDefinition);
    }

    private static LspDiagnostic[]? GetClientDiagnostics(
        IReadOnlyList<LspDiagnostic> diagnostics,
        SourceText sourceText,
        TextSpan unresolvedSpan)
    {
        LspDiagnostic[] matchingDiagnostics =
        [
            .. diagnostics.Where(diagnostic =>
                diagnostic.Code is not null &&
                s_missingUsingDiagnosticIds.Contains(diagnostic.Code) &&
                ToTextSpan(sourceText, diagnostic.Range).IntersectsWith(unresolvedSpan))
        ];
        return matchingDiagnostics.Length == 0 ? null : matchingDiagnostics;
    }

    private static TextSpan ToTextSpan(SourceText sourceText, LspRange range)
    {
        int start = LspPositionConverter.GetOffset(sourceText, range.Start);
        int end = LspPositionConverter.GetOffset(sourceText, range.End);
        return TextSpan.FromBounds(start, Math.Max(start, end));
    }
}
