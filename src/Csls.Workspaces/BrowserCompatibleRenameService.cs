using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Csls.Workspaces;

/// <summary>
/// Renames source symbols without Roslyn's desktop persistent-storage service.
/// </summary>
internal static class BrowserCompatibleRenameService
{
    /// <summary>
    /// Produces a renamed solution by resolving each matching source token semantically.
    /// </summary>
    /// <param name="solution">The source solution.</param>
    /// <param name="symbol">The source symbol to rename.</param>
    /// <param name="newName">The replacement identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The solution containing every semantically matching source edit.</returns>
    internal static async Task<Solution> RenameSymbolAsync(
        Solution solution,
        ISymbol symbol,
        string newName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        string oldName = symbol.Name;
        Solution renamedSolution = solution;
        foreach (Project project in solution.Projects)
        {
            foreach (Document document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken)
                    .ConfigureAwait(false);
                SemanticModel? semanticModel = await document.GetSemanticModelAsync(
                    cancellationToken).ConfigureAwait(false);
                if (root is null || semanticModel is null)
                {
                    continue;
                }

                SyntaxToken[] tokens =
                [
                    .. root.DescendantTokens(descendIntoTrivia: false)
                        .Where(token =>
                            token.IsKind(SyntaxKind.IdentifierToken) &&
                            string.Equals(token.ValueText, oldName, StringComparison.Ordinal) &&
                            IsRenameTarget(semanticModel, token, symbol, cancellationToken))
                ];
                if (tokens.Length == 0)
                {
                    continue;
                }

                SyntaxNode changedRoot = root.ReplaceTokens(
                    tokens,
                    (token, _) => SyntaxFactory.Identifier(
                        token.LeadingTrivia,
                        newName,
                        token.TrailingTrivia));
                renamedSolution = renamedSolution.WithDocumentSyntaxRoot(
                    document.Id,
                    changedRoot);
            }
        }

        return renamedSolution;
    }

    private static bool IsRenameTarget(
        SemanticModel semanticModel,
        SyntaxToken token,
        ISymbol target,
        CancellationToken cancellationToken)
    {
        ISymbol? candidate = FindDeclaredSymbol(
            semanticModel,
            token,
            cancellationToken);
        candidate ??= FindReferencedSymbol(
            semanticModel,
            token,
            cancellationToken);
        return candidate is not null && AreRelated(candidate, target);
    }

    private static ISymbol? FindDeclaredSymbol(
        SemanticModel semanticModel,
        SyntaxToken token,
        CancellationToken cancellationToken)
    {
        for (SyntaxNode? node = token.Parent; node is not null; node = node.Parent)
        {
            ISymbol? symbol = node switch
            {
                BaseTypeDeclarationSyntax declaration
                    when declaration.Identifier == token =>
                    semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                DelegateDeclarationSyntax declaration
                    when declaration.Identifier == token =>
                    semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                MethodDeclarationSyntax declaration
                    when declaration.Identifier == token =>
                    semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                ConstructorDeclarationSyntax declaration
                    when declaration.Identifier == token =>
                    semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                DestructorDeclarationSyntax declaration
                    when declaration.Identifier == token =>
                    semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                PropertyDeclarationSyntax declaration
                    when declaration.Identifier == token =>
                    semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                EventDeclarationSyntax declaration
                    when declaration.Identifier == token =>
                    semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                VariableDeclaratorSyntax declaration
                    when declaration.Identifier == token =>
                    semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                ParameterSyntax declaration
                    when declaration.Identifier == token =>
                    semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                TypeParameterSyntax declaration
                    when declaration.Identifier == token =>
                    semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                LocalFunctionStatementSyntax declaration
                    when declaration.Identifier == token =>
                    semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                LabeledStatementSyntax declaration
                    when declaration.Identifier == token =>
                    semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                SingleVariableDesignationSyntax declaration
                    when declaration.Identifier == token =>
                    semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                ForEachStatementSyntax declaration
                    when declaration.Identifier == token =>
                    semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                CatchDeclarationSyntax declaration
                    when declaration.Identifier == token =>
                    semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                _ => null
            };
            if (symbol is not null)
            {
                return Normalize(symbol);
            }

            if (node is StatementSyntax or MemberDeclarationSyntax)
            {
                break;
            }
        }

        return null;
    }

    private static ISymbol? FindReferencedSymbol(
        SemanticModel semanticModel,
        SyntaxToken token,
        CancellationToken cancellationToken)
    {
        for (SyntaxNode? node = token.Parent; node is not null; node = node.Parent)
        {
            SymbolInfo information = semanticModel.GetSymbolInfo(node, cancellationToken);
            ISymbol? symbol = information.Symbol ?? information.CandidateSymbols.FirstOrDefault();
            if (symbol is not null)
            {
                return Normalize(symbol);
            }

            if (node is StatementSyntax or MemberDeclarationSyntax)
            {
                break;
            }
        }

        return null;
    }

    private static bool AreRelated(ISymbol candidate, ISymbol target)
    {
        candidate = Normalize(candidate);
        target = Normalize(target);
        if (SymbolEqualityComparer.Default.Equals(
            candidate.OriginalDefinition,
            target.OriginalDefinition))
        {
            return true;
        }

        return (candidate, target) switch
        {
            (IMethodSymbol candidateMethod, IMethodSymbol targetMethod) =>
                Overrides(candidateMethod, targetMethod) ||
                Overrides(targetMethod, candidateMethod) ||
                Implements(candidateMethod, targetMethod) ||
                Implements(targetMethod, candidateMethod),
            (IPropertySymbol candidateProperty, IPropertySymbol targetProperty) =>
                Overrides(candidateProperty, targetProperty) ||
                Overrides(targetProperty, candidateProperty) ||
                Implements(candidateProperty, targetProperty) ||
                Implements(targetProperty, candidateProperty),
            (IEventSymbol candidateEvent, IEventSymbol targetEvent) =>
                Overrides(candidateEvent, targetEvent) ||
                Overrides(targetEvent, candidateEvent) ||
                Implements(candidateEvent, targetEvent) ||
                Implements(targetEvent, candidateEvent),
            _ => false
        };
    }

    private static bool Overrides(IMethodSymbol candidate, IMethodSymbol target)
    {
        for (IMethodSymbol? overridden = candidate.OverriddenMethod;
             overridden is not null;
             overridden = overridden.OverriddenMethod)
        {
            if (SymbolEqualityComparer.Default.Equals(
                overridden.OriginalDefinition,
                target.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Overrides(IPropertySymbol candidate, IPropertySymbol target)
    {
        for (IPropertySymbol? overridden = candidate.OverriddenProperty;
             overridden is not null;
             overridden = overridden.OverriddenProperty)
        {
            if (SymbolEqualityComparer.Default.Equals(
                overridden.OriginalDefinition,
                target.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Overrides(IEventSymbol candidate, IEventSymbol target)
    {
        for (IEventSymbol? overridden = candidate.OverriddenEvent;
             overridden is not null;
             overridden = overridden.OverriddenEvent)
        {
            if (SymbolEqualityComparer.Default.Equals(
                overridden.OriginalDefinition,
                target.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Implements(IMethodSymbol candidate, IMethodSymbol target) =>
        candidate.ExplicitInterfaceImplementations.Any(implementation =>
            SymbolEqualityComparer.Default.Equals(
                implementation.OriginalDefinition,
                target.OriginalDefinition)) ||
        ImplementsImplicitly(candidate, target);

    private static bool Implements(IPropertySymbol candidate, IPropertySymbol target) =>
        candidate.ExplicitInterfaceImplementations.Any(implementation =>
            SymbolEqualityComparer.Default.Equals(
                implementation.OriginalDefinition,
                target.OriginalDefinition)) ||
        ImplementsImplicitly(candidate, target);

    private static bool Implements(IEventSymbol candidate, IEventSymbol target) =>
        candidate.ExplicitInterfaceImplementations.Any(implementation =>
            SymbolEqualityComparer.Default.Equals(
                implementation.OriginalDefinition,
                target.OriginalDefinition)) ||
        ImplementsImplicitly(candidate, target);

    private static bool ImplementsImplicitly(ISymbol candidate, ISymbol target)
    {
        if (target.ContainingType?.TypeKind != TypeKind.Interface ||
            candidate.ContainingType is null)
        {
            return false;
        }

        ISymbol? implementation = candidate.ContainingType.FindImplementationForInterfaceMember(
            target);
        return implementation is not null && SymbolEqualityComparer.Default.Equals(
            Normalize(implementation).OriginalDefinition,
            candidate.OriginalDefinition);
    }

    private static ISymbol Normalize(ISymbol symbol) => symbol switch
    {
        IMethodSymbol
        {
            MethodKind: MethodKind.Constructor or
                MethodKind.StaticConstructor or
                MethodKind.Destructor,
            ContainingType: { } containingType
        } => containingType,
        IMethodSymbol
        {
            AssociatedSymbol: { } associatedSymbol
        } => associatedSymbol,
        _ => symbol
    };
}
