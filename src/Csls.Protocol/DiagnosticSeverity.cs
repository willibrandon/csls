namespace Csls.Protocol;

/// <summary>
/// Identifies the editor-facing severity assigned to one language diagnostic.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>
    /// Indicates that no LSP diagnostic severity was assigned.
    /// </summary>
    None = 0,

    /// <summary>
    /// Indicates a compilation or analysis error.
    /// </summary>
    Error = 1,

    /// <summary>
    /// Indicates a compilation or analysis warning.
    /// </summary>
    Warning = 2,

    /// <summary>
    /// Indicates an informational analysis result.
    /// </summary>
    Information = 3,

    /// <summary>
    /// Indicates a low-priority analysis hint.
    /// </summary>
    Hint = 4
}
