namespace Csls.Protocol;

/// <summary>
/// Identifies additional editor behavior associated with one diagnostic.
/// </summary>
public enum DiagnosticTag
{
    /// <summary>
    /// Indicates that no LSP diagnostic tag was assigned.
    /// </summary>
    None = 0,

    /// <summary>
    /// Indicates code that the editor may render as unnecessary.
    /// </summary>
    Unnecessary = 1,

    /// <summary>
    /// Indicates code that the editor may render as deprecated.
    /// </summary>
    Deprecated = 2
}
