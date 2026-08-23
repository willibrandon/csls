using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using LspRange = Csls.Protocol.Range;
using LspSymbolKind = Csls.Protocol.SymbolKind;
using RoslynLocation = Microsoft.CodeAnalysis.Location;

namespace Csls.Workspaces;

/// <summary>
/// Computes bounded call and type hierarchies from immutable Roslyn snapshots.
/// </summary>
internal static class WorkspaceHierarchyService
{
    private const int MaximumHierarchyItems = 500;

    /// <summary>
    /// Prepares the callable source declaration at one document position.
    /// </summary>
    /// <param name="document">The current immutable source document.</param>
    /// <param name="position">The target UTF-16 position.</param>
    /// <param name="generation">The captured workspace generation.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The prepared item, or an empty list when no callable symbol exists.</returns>
    internal static async Task<IReadOnlyList<CallHierarchyItem>> PrepareCallHierarchyAsync(
        Document? document,
        Position position,
        long generation,
        CancellationToken cancellationToken)
    {
        ISymbol? symbol = await FindSymbolAsync(document, position, cancellationToken)
            .ConfigureAwait(false);
        CallHierarchyItem? item = document is null || symbol is null
            ? null
            : await CreateCallItemAsync(
                NormalizeCallableSymbol(symbol),
                document.Project.Solution,
                generation,
                cancellationToken).ConfigureAwait(false);
        return item is null ? [] : [item];
    }

    /// <summary>
    /// Finds direct source callers for one callable symbol.
    /// </summary>
    /// <param name="document">The source document containing the callable declaration.</param>
    /// <param name="position">The callable declaration identifier position.</param>
    /// <param name="generation">The captured workspace generation.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded direct incoming calls.</returns>
    internal static async Task<IReadOnlyList<CallHierarchyIncomingCall>> GetIncomingCallsAsync(
        Document document,
        Position position,
        long generation,
        CancellationToken cancellationToken)
    {
        ISymbol? symbol = NormalizeCallableSymbol(
            await FindSymbolAsync(document, position, cancellationToken).ConfigureAwait(false));
        if (!SupportsCallHierarchy(symbol))
        {
            return [];
        }

        IEnumerable<SymbolCallerInfo> callers = await SymbolFinder.FindCallersAsync(
            symbol!,
            document.Project.Solution,
            cancellationToken).ConfigureAwait(false);
        var results = new Dictionary<string, CallHierarchyIncomingCall>(StringComparer.Ordinal);
        foreach (SymbolCallerInfo caller in callers.Where(static caller => caller.IsDirect))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ISymbol? callingSymbol = NormalizeCallableSymbol(caller.CallingSymbol);
            CallHierarchyItem? item = await CreateCallItemAsync(
                callingSymbol,
                document.Project.Solution,
                generation,
                cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                continue;
            }

            IReadOnlyList<LspRange> ranges = CreateSourceRanges(caller.Locations, item.Uri);
            if (ranges.Count == 0)
            {
                continue;
            }

            string key = CreateItemKey(item.Uri, item.SelectionRange.Start);
            if (results.TryGetValue(key, out CallHierarchyIncomingCall? existing))
            {
                ranges = OrderRanges(existing.FromRanges.Concat(ranges));
            }

            results[key] = new CallHierarchyIncomingCall
            {
                From = item,
                FromRanges = ranges
            };
            if (results.Count >= MaximumHierarchyItems)
            {
                break;
            }
        }

        return OrderIncomingCalls(results.Values);
    }

    /// <summary>
    /// Finds direct source callees within one callable declaration.
    /// </summary>
    /// <param name="document">The source document containing the callable declaration.</param>
    /// <param name="position">The callable declaration identifier position.</param>
    /// <param name="generation">The captured workspace generation.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded direct outgoing calls.</returns>
    internal static async Task<IReadOnlyList<CallHierarchyOutgoingCall>> GetOutgoingCallsAsync(
        Document document,
        Position position,
        long generation,
        CancellationToken cancellationToken)
    {
        ISymbol? symbol = NormalizeCallableSymbol(
            await FindSymbolAsync(document, position, cancellationToken).ConfigureAwait(false));
        if (!SupportsCallHierarchy(symbol))
        {
            return [];
        }

        var results = new Dictionary<string, CallHierarchyOutgoingCall>(StringComparer.Ordinal);
        foreach (SyntaxReference declarationReference in symbol!.DeclaringSyntaxReferences)
        {
            Document? declarationDocument = document.Project.Solution.GetDocument(
                declarationReference.SyntaxTree);
            if (declarationDocument is null)
            {
                continue;
            }

            SemanticModel semanticModel = await declarationDocument
                .GetSemanticModelAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Roslyn returned no semantic model.");
            SyntaxNode declaration = await declarationReference
                .GetSyntaxAsync(cancellationToken)
                .ConfigureAwait(false);
            await AddOutgoingCallsAsync(
                declaration,
                declarationDocument,
                semanticModel,
                generation,
                results,
                cancellationToken).ConfigureAwait(false);
            if (results.Count >= MaximumHierarchyItems)
            {
                break;
            }
        }

        return OrderOutgoingCalls(results.Values);
    }

    /// <summary>
    /// Prepares the named source type at one document position.
    /// </summary>
    /// <param name="document">The current immutable source document.</param>
    /// <param name="position">The target UTF-16 position.</param>
    /// <param name="generation">The captured workspace generation.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The prepared item, or an empty list when no named type exists.</returns>
    internal static async Task<IReadOnlyList<TypeHierarchyItem>> PrepareTypeHierarchyAsync(
        Document? document,
        Position position,
        long generation,
        CancellationToken cancellationToken)
    {
        ISymbol? symbol = await FindSymbolAsync(document, position, cancellationToken)
            .ConfigureAwait(false);
        INamedTypeSymbol? type = GetNamedType(symbol);
        TypeHierarchyItem? item = document is null || type is null
            ? null
            : await CreateTypeItemAsync(
                type,
                document.Project.Solution,
                generation,
                cancellationToken).ConfigureAwait(false);
        return item is null ? [] : [item];
    }

    /// <summary>
    /// Finds direct source supertypes for one named type.
    /// </summary>
    /// <param name="document">The source document containing the type declaration.</param>
    /// <param name="position">The type declaration identifier position.</param>
    /// <param name="generation">The captured workspace generation.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded direct source supertypes.</returns>
    internal static async Task<IReadOnlyList<TypeHierarchyItem>> GetSupertypesAsync(
        Document document,
        Position position,
        long generation,
        CancellationToken cancellationToken)
    {
        INamedTypeSymbol? type = GetNamedType(
            await FindSymbolAsync(document, position, cancellationToken).ConfigureAwait(false));
        if (type is null)
        {
            return [];
        }

        IEnumerable<INamedTypeSymbol> supertypes = type.BaseType is null
            ? type.Interfaces
            : type.Interfaces.Prepend(type.BaseType);
        return await CreateTypeItemsAsync(
            supertypes,
            document.Project.Solution,
            generation,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds direct source subtypes for one named type.
    /// </summary>
    /// <param name="document">The source document containing the type declaration.</param>
    /// <param name="position">The type declaration identifier position.</param>
    /// <param name="generation">The captured workspace generation.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded direct source subtypes.</returns>
    internal static async Task<IReadOnlyList<TypeHierarchyItem>> GetSubtypesAsync(
        Document document,
        Position position,
        long generation,
        CancellationToken cancellationToken)
    {
        INamedTypeSymbol? type = GetNamedType(
            await FindSymbolAsync(document, position, cancellationToken).ConfigureAwait(false));
        if (type is null)
        {
            return [];
        }

        Solution solution = document.Project.Solution;
        IEnumerable<INamedTypeSymbol> subtypes;
        if (type.TypeKind == TypeKind.Interface)
        {
            IEnumerable<INamedTypeSymbol> derivedInterfaces =
                await SymbolFinder.FindDerivedInterfacesAsync(
                    type,
                    solution,
                    transitive: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            IEnumerable<INamedTypeSymbol> implementations =
                await SymbolFinder.FindImplementationsAsync(
                    type,
                    solution,
                    transitive: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            subtypes = derivedInterfaces.Concat(implementations);
        }
        else
        {
            subtypes = await SymbolFinder.FindDerivedClassesAsync(
                type,
                solution,
                transitive: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return await CreateTypeItemsAsync(
            subtypes,
            solution,
            generation,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddOutgoingCallsAsync(
        SyntaxNode declaration,
        Document document,
        SemanticModel semanticModel,
        long generation,
        Dictionary<string, CallHierarchyOutgoingCall> results,
        CancellationToken cancellationToken)
    {
        var seenOperations = new HashSet<(TextSpan Span, string Symbol)>(
            EqualityComparer<(TextSpan Span, string Symbol)>.Default);
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        foreach (SyntaxNode node in declaration.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
            IOperation? operation = semanticModel.GetOperation(node, cancellationToken);
            ISymbol? referencedSymbol = NormalizeCallableSymbol(GetReferencedSymbol(operation));
            if (!SupportsCallHierarchy(referencedSymbol) || operation is null)
            {
                continue;
            }

            string symbolKey = referencedSymbol!.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat);
            if (!seenOperations.Add((operation.Syntax.Span, symbolKey)))
            {
                continue;
            }

            CallHierarchyItem? item = await CreateCallItemAsync(
                referencedSymbol,
                document.Project.Solution,
                generation,
                cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                continue;
            }

            LspRange callRange = ToRange(text, operation.Syntax.Span);
            string key = CreateItemKey(item.Uri, item.SelectionRange.Start);
            IReadOnlyList<LspRange> ranges = results.TryGetValue(
                key,
                out CallHierarchyOutgoingCall? existing)
                ? OrderRanges(existing.FromRanges.Append(callRange))
                : [callRange];
            results[key] = new CallHierarchyOutgoingCall
            {
                To = item,
                FromRanges = ranges
            };
            if (results.Count >= MaximumHierarchyItems)
            {
                return;
            }
        }
    }

    private static ISymbol? GetReferencedSymbol(IOperation? operation) => operation switch
    {
        IInvocationOperation invocation => invocation.TargetMethod,
        IObjectCreationOperation objectCreation => objectCreation.Constructor,
        IPropertyReferenceOperation propertyReference => propertyReference.Property,
        IEventReferenceOperation eventReference => eventReference.Event,
        IFieldReferenceOperation fieldReference => fieldReference.Field,
        _ => null
    };

    private static async Task<ISymbol?> FindSymbolAsync(
        Document? document,
        Position position,
        CancellationToken cancellationToken)
    {
        if (document is null)
        {
            return null;
        }

        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        SemanticModel semanticModel = await document
            .GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no semantic model.");
        return await SymbolFinder.FindSymbolAtPositionAsync(
            semanticModel,
            GetOffset(text, position),
            document.Project.Solution.Workspace,
            cancellationToken).ConfigureAwait(false);
    }

    private static ISymbol? NormalizeCallableSymbol(ISymbol? symbol)
    {
        if (symbol is IMethodSymbol { AssociatedSymbol: not null } accessor)
        {
            symbol = accessor.AssociatedSymbol;
        }

        return SupportsCallHierarchy(symbol) ? symbol!.OriginalDefinition : null;
    }

    private static bool SupportsCallHierarchy(ISymbol? symbol) =>
        symbol is IMethodSymbol or IPropertySymbol or IEventSymbol or IFieldSymbol;

    private static INamedTypeSymbol? GetNamedType(ISymbol? symbol) => symbol switch
    {
        INamedTypeSymbol type => type.OriginalDefinition,
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor }
            method => method.ContainingType.OriginalDefinition,
        ILocalSymbol { Type: INamedTypeSymbol type } => type.OriginalDefinition,
        IParameterSymbol { Type: INamedTypeSymbol type } => type.OriginalDefinition,
        IFieldSymbol { Type: INamedTypeSymbol type } => type.OriginalDefinition,
        IPropertySymbol { Type: INamedTypeSymbol type } => type.OriginalDefinition,
        IEventSymbol { Type: INamedTypeSymbol type } => type.OriginalDefinition,
        null => null,
        _ => null
    };

    private static async Task<CallHierarchyItem?> CreateCallItemAsync(
        ISymbol? symbol,
        Solution solution,
        long generation,
        CancellationToken cancellationToken)
    {
        if (!SupportsCallHierarchy(symbol))
        {
            return null;
        }

        (SyntaxNode Declaration, RoslynLocation Selection, Document Document)? source =
            await FindSourceDeclarationAsync(symbol!, solution, cancellationToken)
                .ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        SourceText text = await source.Value.Document
            .GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        LspRange selectionRange = ToRange(text, source.Value.Selection.SourceSpan);
        var uri = DocumentUri.FromFileSystemPath(
            source.Value.Document.FilePath
                ?? throw new InvalidOperationException("A source hierarchy document has no path."));
        return new CallHierarchyItem
        {
            Name = GetCallItemName(symbol!),
            Kind = GetSymbolKind(symbol!),
            Detail = symbol!.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Uri = uri,
            Range = ToRange(text, source.Value.Declaration.Span),
            SelectionRange = selectionRange,
            Data = new HierarchyItemData
            {
                Generation = generation,
                Uri = uri,
                Position = selectionRange.Start
            }
        };
    }

    private static async Task<TypeHierarchyItem?> CreateTypeItemAsync(
        INamedTypeSymbol type,
        Solution solution,
        long generation,
        CancellationToken cancellationToken)
    {
        (SyntaxNode Declaration, RoslynLocation Selection, Document Document)? source =
            await FindSourceDeclarationAsync(type, solution, cancellationToken)
                .ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        SourceText text = await source.Value.Document
            .GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        LspRange selectionRange = ToRange(text, source.Value.Selection.SourceSpan);
        var uri = DocumentUri.FromFileSystemPath(
            source.Value.Document.FilePath
                ?? throw new InvalidOperationException("A source hierarchy document has no path."));
        return new TypeHierarchyItem
        {
            Name = type.Name,
            Kind = GetSymbolKind(type),
            Detail = type.ContainingNamespace.IsGlobalNamespace
                ? null
                : type.ContainingNamespace.ToDisplayString(),
            Uri = uri,
            Range = ToRange(text, source.Value.Declaration.Span),
            SelectionRange = selectionRange,
            Data = new HierarchyItemData
            {
                Generation = generation,
                Uri = uri,
                Position = selectionRange.Start
            }
        };
    }

    private static async Task<
        (SyntaxNode Declaration, RoslynLocation Selection, Document Document)?>
        FindSourceDeclarationAsync(
            ISymbol symbol,
            Solution solution,
            CancellationToken cancellationToken)
    {
        ISymbol sourceSymbol = await SymbolFinder.FindSourceDefinitionAsync(
            symbol,
            solution,
            cancellationToken).ConfigureAwait(false) ?? symbol;
        RoslynLocation? selection = sourceSymbol.Locations
            .Where(static location => location.IsInSource)
            .OrderBy(static location => location.SourceTree?.FilePath, StringComparer.Ordinal)
            .ThenBy(static location => location.SourceSpan.Start)
            .FirstOrDefault();
        if (selection?.SourceTree is null)
        {
            return null;
        }

        Document? document = solution.GetDocument(selection.SourceTree);
        SyntaxReference? declarationReference = sourceSymbol.DeclaringSyntaxReferences
            .FirstOrDefault(reference =>
                reference.SyntaxTree == selection.SourceTree &&
                reference.Span.Contains(selection.SourceSpan));
        if (document is null || declarationReference is null)
        {
            return null;
        }

        SyntaxNode declaration = await declarationReference
            .GetSyntaxAsync(cancellationToken)
            .ConfigureAwait(false);
        return (declaration, selection, document);
    }

    private static async Task<IReadOnlyList<TypeHierarchyItem>> CreateTypeItemsAsync(
        IEnumerable<INamedTypeSymbol> symbols,
        Solution solution,
        long generation,
        CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, TypeHierarchyItem>(StringComparer.Ordinal);
        foreach (INamedTypeSymbol symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TypeHierarchyItem? item = await CreateTypeItemAsync(
                symbol,
                solution,
                generation,
                cancellationToken).ConfigureAwait(false);
            if (item is not null)
            {
                items[CreateItemKey(item.Uri, item.SelectionRange.Start)] = item;
            }

            if (items.Count >= MaximumHierarchyItems)
            {
                break;
            }
        }

        return
        [
            .. items.Values
                .OrderBy(static item => item.Name, StringComparer.Ordinal)
                .ThenBy(static item => item.Uri.ToString(), StringComparer.Ordinal)
                .ThenBy(static item => item.SelectionRange.Start.Line)
                .ThenBy(static item => item.SelectionRange.Start.Character)
        ];
    }

    private static IReadOnlyList<LspRange> CreateSourceRanges(
        IEnumerable<RoslynLocation> locations,
        DocumentUri expectedUri)
    {
        var ranges = new HashSet<LspRange>();
        foreach (RoslynLocation location in locations)
        {
            string? path = location.SourceTree?.FilePath;
            if (!location.IsInSource || string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (DocumentUri.FromFileSystemPath(path) != expectedUri)
            {
                continue;
            }

            FileLinePositionSpan lineSpan = location.GetLineSpan();
            ranges.Add(new LspRange(
                new Position(lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character),
                new Position(lineSpan.EndLinePosition.Line, lineSpan.EndLinePosition.Character)));
        }

        return OrderRanges(ranges);
    }

    private static IReadOnlyList<LspRange> OrderRanges(IEnumerable<LspRange> ranges) =>
    [
        .. ranges
            .Distinct()
            .OrderBy(static range => range.Start.Line)
            .ThenBy(static range => range.Start.Character)
            .ThenBy(static range => range.End.Line)
            .ThenBy(static range => range.End.Character)
            .Take(MaximumHierarchyItems)
    ];

    private static IReadOnlyList<CallHierarchyIncomingCall> OrderIncomingCalls(
        IEnumerable<CallHierarchyIncomingCall> calls) =>
    [
        .. calls
            .OrderBy(static call => call.From.Name, StringComparer.Ordinal)
            .ThenBy(static call => call.From.Uri.ToString(), StringComparer.Ordinal)
            .ThenBy(static call => call.From.SelectionRange.Start.Line)
            .ThenBy(static call => call.From.SelectionRange.Start.Character)
    ];

    private static IReadOnlyList<CallHierarchyOutgoingCall> OrderOutgoingCalls(
        IEnumerable<CallHierarchyOutgoingCall> calls) =>
    [
        .. calls
            .OrderBy(static call => call.To.Name, StringComparer.Ordinal)
            .ThenBy(static call => call.To.Uri.ToString(), StringComparer.Ordinal)
            .ThenBy(static call => call.To.SelectionRange.Start.Line)
            .ThenBy(static call => call.To.SelectionRange.Start.Character)
    ];

    private static string GetCallItemName(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor }
            method => method.ContainingType.Name,
        _ => symbol.Name
    };

    private static LspSymbolKind GetSymbolKind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => LspSymbolKind.Struct,
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => LspSymbolKind.Interface,
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => LspSymbolKind.Enum,
        INamedTypeSymbol { TypeKind: TypeKind.Delegate } => LspSymbolKind.Function,
        INamedTypeSymbol => LspSymbolKind.Class,
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } =>
            LspSymbolKind.Constructor,
        IMethodSymbol { MethodKind: MethodKind.LocalFunction } => LspSymbolKind.Function,
        IMethodSymbol => LspSymbolKind.Method,
        IPropertySymbol => LspSymbolKind.Property,
        IEventSymbol => LspSymbolKind.Event,
        IFieldSymbol { IsConst: true } => LspSymbolKind.Constant,
        IFieldSymbol => LspSymbolKind.Field,
        _ => LspSymbolKind.ObjectValue
    };

    private static string CreateItemKey(DocumentUri uri, Position position) =>
        $"{uri}\u001f{position.Line}\u001f{position.Character}";

    private static LspRange ToRange(SourceText text, TextSpan span)
    {
        LinePositionSpan lineSpan = text.Lines.GetLinePositionSpan(span);
        return new LspRange(
            new Position(lineSpan.Start.Line, lineSpan.Start.Character),
            new Position(lineSpan.End.Line, lineSpan.End.Character));
    }

    private static int GetOffset(SourceText text, Position position)
    {
        if (position.Line >= text.Lines.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                position,
                "The position line is outside the document.");
        }

        TextLine line = text.Lines[position.Line];
        if (position.Character > line.Span.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                position,
                "The position character is outside the line.");
        }

        return line.Start + position.Character;
    }
}
