namespace Csls.Protocol;

/// <summary>
/// Describes one source selection and its next enclosing syntax selection.
/// </summary>
public sealed record SelectionRange
{
    /// <summary>
    /// Gets the source range selected at this hierarchy level.
    /// </summary>
    public required Range Range { get; init; }

    /// <summary>
    /// Gets the next enclosing selection, when one exists.
    /// </summary>
    public SelectionRange? Parent { get; init; }
}
