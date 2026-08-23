namespace Csls.Protocol;

/// <summary>
/// Identifies the semantic access performed by one document highlight.
/// </summary>
public enum DocumentHighlightKind
{
    /// <summary>
    /// Indicates that no protocol highlight classification is present.
    /// </summary>
    None = 0,

    /// <summary>
    /// Indicates an occurrence without a more specific access classification.
    /// </summary>
    Text = 1,

    /// <summary>
    /// Indicates an occurrence that reads the symbol value.
    /// </summary>
    Read = 2,

    /// <summary>
    /// Indicates an occurrence that writes the symbol value.
    /// </summary>
    Write = 3
}
