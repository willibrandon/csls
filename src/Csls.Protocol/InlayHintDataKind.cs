namespace Csls.Protocol;

/// <summary>
/// Identifies the server-owned data required to resolve an inlay hint.
/// </summary>
public enum InlayHintDataKind
{
    /// <summary>
    /// Identifies an inferred local variable type hint.
    /// </summary>
    LocalType,

    /// <summary>
    /// Identifies an argument parameter-name hint.
    /// </summary>
    ParameterName
}
