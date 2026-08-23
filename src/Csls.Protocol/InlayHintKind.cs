namespace Csls.Protocol;

/// <summary>
/// Identifies the semantic category of an inlay hint.
/// </summary>
public enum InlayHintKind
{
    /// <summary>
    /// Indicates no more specific inlay-hint category.
    /// </summary>
    None = 0,

    /// <summary>
    /// Identifies an inferred type annotation.
    /// </summary>
    Type = 1,

    /// <summary>
    /// Identifies an argument parameter-name annotation.
    /// </summary>
    Parameter = 2
}
