using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace Csls.Workspaces;

/// <summary>
/// Creates stable .NET monikers for symbols resolved from immutable Roslyn snapshots.
/// </summary>
internal static class WorkspaceMonikerService
{
    private const string Scheme = "dotnet";

    /// <summary>
    /// Gets the symbol moniker at one UTF-16 document position.
    /// </summary>
    /// <param name="document">The resolved Roslyn document, when present.</param>
    /// <param name="position">The target UTF-16 position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The resolved moniker, or an empty list when no stable identity exists.</returns>
    internal static async Task<IReadOnlyList<Moniker>> GetMonikersAsync(
        Document? document,
        Position position,
        CancellationToken cancellationToken)
    {
        if (document is null)
        {
            return [];
        }

        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        int offset = LspPositionConverter.GetOffset(text, position);
        SemanticModel semanticModel = await document
            .GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no semantic model.");
        ISymbol? symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            semanticModel,
            offset,
            document.Project.Solution.Workspace,
            cancellationToken).ConfigureAwait(false);
        if (symbol is null)
        {
            return [];
        }

        symbol = NormalizeSymbol(symbol);
        if (symbol is INamespaceSymbol or IDiscardSymbol or ITypeSymbol { TypeKind: TypeKind.Error })
        {
            return [];
        }

        IAssemblySymbol currentAssembly = semanticModel.Compilation.Assembly;
        string? identifier = CreateIdentifier(symbol, currentAssembly);
        if (identifier is null)
        {
            return [];
        }

        (MonikerKind kind, UniquenessLevel unique) = Classify(
            symbol,
            currentAssembly);
        return
        [
            new Moniker
            {
                Scheme = Scheme,
                Identifier = identifier,
                Unique = unique,
                Kind = kind
            }
        ];
    }

    private static ISymbol NormalizeSymbol(ISymbol symbol)
    {
        ISymbol target = symbol is IAliasSymbol alias ? alias.Target : symbol;
        if (target is IMethodSymbol { ReducedFrom: not null } method)
        {
            target = method.ReducedFrom;
        }

        return target.OriginalDefinition;
    }

    private static string? CreateIdentifier(ISymbol symbol, IAssemblySymbol currentAssembly)
    {
        IAssemblySymbol assembly = symbol.ContainingAssembly ?? currentAssembly;
        string assemblyIdentity = assembly.Identity.GetDisplayName();
        switch (symbol)
        {
            case IParameterSymbol parameter:
                return CreateParameterIdentifier(assemblyIdentity, parameter);
            case ITypeParameterSymbol typeParameter:
                return CreateTypeParameterIdentifier(assemblyIdentity, typeParameter);
            case ILocalSymbol or ILabelSymbol or IRangeVariableSymbol:
                return CreateDocumentIdentifier(assemblyIdentity, symbol);
        }

        string? documentationId = symbol.GetDocumentationCommentId();
        return string.IsNullOrEmpty(documentationId)
            ? null
            : $"{assemblyIdentity}::{documentationId}";
    }

    private static string? CreateParameterIdentifier(
        string assemblyIdentity,
        IParameterSymbol parameter)
    {
        string? containingId = parameter.ContainingSymbol.GetDocumentationCommentId();
        return string.IsNullOrEmpty(containingId)
            ? null
            : $"{assemblyIdentity}::{containingId}#parameter/{parameter.Ordinal}";
    }

    private static string? CreateTypeParameterIdentifier(
        string assemblyIdentity,
        ITypeParameterSymbol typeParameter)
    {
        string? containingId = typeParameter.ContainingSymbol.GetDocumentationCommentId();
        return string.IsNullOrEmpty(containingId)
            ? null
            : $"{assemblyIdentity}::{containingId}#type-parameter/{typeParameter.Ordinal}";
    }

    private static string? CreateDocumentIdentifier(
        string assemblyIdentity,
        ISymbol symbol)
    {
        string? containingId = symbol.ContainingSymbol?.GetDocumentationCommentId();
        SyntaxReference? declaration = GetFirstDeclaration(symbol);
        return string.IsNullOrEmpty(containingId) || declaration is null
            ? null
            : $"{assemblyIdentity}::{containingId}#{symbol.Kind}/{symbol.Name}/{declaration.Span.Start}";
    }

    private static SyntaxReference? GetFirstDeclaration(ISymbol symbol)
    {
        SyntaxReference? first = null;
        foreach (SyntaxReference candidate in symbol.DeclaringSyntaxReferences)
        {
            if (first is null ||
                StringComparer.Ordinal.Compare(
                    candidate.SyntaxTree.FilePath,
                    first.SyntaxTree.FilePath) < 0 ||
                (StringComparer.Ordinal.Equals(
                    candidate.SyntaxTree.FilePath,
                    first.SyntaxTree.FilePath) &&
                    candidate.Span.Start < first.Span.Start))
            {
                first = candidate;
            }
        }

        return first;
    }

    private static (MonikerKind Kind, UniquenessLevel Unique) Classify(
        ISymbol symbol,
        IAssemblySymbol currentAssembly)
    {
        if (symbol.ContainingAssembly is not null &&
            !SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, currentAssembly))
        {
            return (
                MonikerKind.Import,
                GetAssemblyUniqueness(symbol.ContainingAssembly.Identity));
        }

        if (symbol is ILocalSymbol or ILabelSymbol or IRangeVariableSymbol)
        {
            return (MonikerKind.Local, UniquenessLevel.Document);
        }

        return IsExternallyVisible(symbol)
            ? (
                MonikerKind.Export,
                GetAssemblyUniqueness(currentAssembly.Identity))
            : (MonikerKind.Local, UniquenessLevel.Project);
    }

    private static UniquenessLevel GetAssemblyUniqueness(AssemblyIdentity identity) =>
        identity.IsStrongName ? UniquenessLevel.Scheme : UniquenessLevel.Group;

    private static bool IsExternallyVisible(ISymbol symbol)
    {
        ISymbol? current = symbol;
        while (current is IParameterSymbol or ITypeParameterSymbol)
        {
            current = current.ContainingSymbol;
        }

        for (; current is not null; current = current.ContainingType)
        {
            if (current is INamespaceSymbol or IAssemblySymbol)
            {
                break;
            }

            if (current.DeclaredAccessibility is not (
                Accessibility.Public or
                Accessibility.Protected or
                Accessibility.ProtectedOrInternal))
            {
                return false;
            }
        }

        return true;
    }
}
