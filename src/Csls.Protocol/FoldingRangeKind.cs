namespace Csls.Protocol;

/// <summary>
/// Defines the standard LSP semantic categories for folding ranges.
/// </summary>
public static class FoldingRangeKind
{
    /// <summary>
    /// Identifies a comment folding range.
    /// </summary>
    public const string Comment = "comment";

    /// <summary>
    /// Identifies an import folding range.
    /// </summary>
    public const string Imports = "imports";

    /// <summary>
    /// Identifies a region-directive folding range.
    /// </summary>
    public const string Region = "region";
}
