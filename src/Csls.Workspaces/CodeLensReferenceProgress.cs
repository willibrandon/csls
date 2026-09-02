using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace Csls.Workspaces;

/// <summary>
/// Collects distinct source references and cancels Roslyn after an optional count cap.
/// </summary>
internal sealed class CodeLensReferenceProgress : IFindReferencesProgress, IDisposable
{
    private readonly CancellationTokenSource _cancellationSource;
    private readonly HashSet<(SyntaxTree Tree, TextSpan Span)> _locations = [];
    private readonly Lock _gate = new();
    private readonly SyntaxNode _queriedNode;
    private readonly ISymbol _queriedSymbol;
    private readonly int _searchCap;

    /// <summary>
    /// Initializes reference collection for one declared symbol and optional count cap.
    /// </summary>
    /// <param name="queriedSymbol">The declared symbol whose references are requested.</param>
    /// <param name="queriedNode">The declaration syntax that owns the code lens.</param>
    /// <param name="searchCap">The exact-count cap, or zero to collect every reference.</param>
    /// <param name="cancellationToken">The caller cancellation token.</param>
    internal CodeLensReferenceProgress(
        ISymbol queriedSymbol,
        SyntaxNode queriedNode,
        int searchCap,
        CancellationToken cancellationToken)
    {
        _queriedSymbol = queriedSymbol;
        _queriedNode = queriedNode;
        _searchCap = searchCap;
        _cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    /// <summary>
    /// Gets the token canceled by the caller or after the reference cap is exceeded.
    /// </summary>
    internal CancellationToken CancellationToken => _cancellationSource.Token;

    /// <summary>
    /// Gets whether collection found more references than the configured exact-count cap.
    /// </summary>
    internal bool SearchCapReached
    {
        get
        {
            lock (_gate)
            {
                return _searchCap > 0 && _locations.Count > _searchCap;
            }
        }
    }

    /// <summary>
    /// Gets the number of distinct source references collected so far.
    /// </summary>
    internal int ReferenceCount
    {
        get
        {
            lock (_gate)
            {
                return _locations.Count;
            }
        }
    }

    /// <summary>
    /// Gets a stable snapshot of the distinct collected source locations.
    /// </summary>
    internal IReadOnlyList<Location> Locations
    {
        get
        {
            lock (_gate)
            {
                return
                [
                    .. _locations
                        .OrderBy(static item => item.Tree.FilePath, StringComparer.Ordinal)
                        .ThenBy(static item => item.Span.Start)
                        .Select(static item => Location.Create(item.Tree, item.Span))
                ];
            }
        }
    }

    void IFindReferencesProgress.OnStarted()
    {
    }

    void IFindReferencesProgress.OnCompleted()
    {
    }

    void IFindReferencesProgress.OnFindInDocumentStarted(Document document)
    {
    }

    void IFindReferencesProgress.OnFindInDocumentCompleted(Document document)
    {
    }

    void IFindReferencesProgress.OnDefinitionFound(ISymbol symbol)
    {
        if (symbol.IsImplicitlyDeclared ||
            symbol is IMethodSymbol { AssociatedSymbol: not null })
        {
            return;
        }

        bool includesQueriedDefinition = symbol.Locations.Any(candidate =>
            candidate.IsInSource &&
            _queriedSymbol.Locations.Any(queried => SameSourceLocation(candidate, queried)));
        if (includesQueriedDefinition)
        {
            foreach (SyntaxReference syntaxReference in symbol.DeclaringSyntaxReferences)
            {
                SyntaxNode declaration = syntaxReference.GetSyntax(CancellationToken);
                if (declaration.SyntaxTree == _queriedNode.SyntaxTree &&
                    declaration.Span == _queriedNode.Span)
                {
                    continue;
                }

                AddLocation(declaration.GetLocation());
            }

            return;
        }

        foreach (Location location in symbol.Locations)
        {
            AddLocation(location);
        }
    }

    void IFindReferencesProgress.OnReferenceFound(
        ISymbol symbol,
        ReferenceLocation location)
    {
        bool constructorInvocation = _queriedSymbol.Kind == SymbolKind.NamedType &&
            symbol is IMethodSymbol { MethodKind: MethodKind.Constructor };
        bool cannotBeReferencedByName = symbol.IsImplicitlyDeclared ||
            symbol is IMethodSymbol { AssociatedSymbol: not null };
        if (cannotBeReferencedByName && !constructorInvocation ||
            !symbol.Locations.Any(static candidate => candidate.IsInSource))
        {
            return;
        }

        AddLocation(location.Location);
    }

    void IFindReferencesProgress.ReportProgress(int current, int maximum)
    {
    }

    /// <summary>
    /// Releases the linked cancellation source used by the reference search.
    /// </summary>
    public void Dispose()
    {
        _cancellationSource.Dispose();
    }

    private void AddLocation(Location location)
    {
        SyntaxTree? tree = location.SourceTree;
        if (!location.IsInSource || tree is null)
        {
            return;
        }

        bool capReached;
        lock (_gate)
        {
            _locations.Add((tree, location.SourceSpan));
            capReached = _searchCap > 0 && _locations.Count > _searchCap;
        }

        if (capReached)
        {
            _cancellationSource.Cancel();
        }
    }

    private static bool SameSourceLocation(Location left, Location right) =>
        left.SourceTree == right.SourceTree && left.SourceSpan == right.SourceSpan;
}
