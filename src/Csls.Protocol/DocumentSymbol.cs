namespace Csls.Protocol;

/// <summary>
/// Describes one hierarchical declaration within a source document.
/// </summary>
public sealed record DocumentSymbol
{
    /// <summary>
    /// Gets the source declaration name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets supplemental signature or type information.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Gets the declaration category.
    /// </summary>
    public required SymbolKind Kind { get; init; }

    /// <summary>
    /// Gets the complete declaration range.
    /// </summary>
    public required Range Range { get; init; }

    /// <summary>
    /// Gets the range that most closely identifies the declaration name.
    /// </summary>
    public required Range SelectionRange { get; init; }

    /// <summary>
    /// Gets nested declarations in source order.
    /// </summary>
    public IReadOnlyList<DocumentSymbol>? Children { get; init; }
}
