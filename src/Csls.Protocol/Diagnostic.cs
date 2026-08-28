namespace Csls.Protocol;

/// <summary>
/// Describes one compiler or analyzer finding associated with a source range.
/// </summary>
public sealed record Diagnostic
{
    /// <summary>
    /// Gets the exact source range associated with the finding.
    /// </summary>
    public required Range Range { get; init; }

    /// <summary>
    /// Gets the editor-facing diagnostic severity.
    /// </summary>
    public DiagnosticSeverity? Severity { get; init; }

    /// <summary>
    /// Gets the compiler or analyzer diagnostic identifier.
    /// </summary>
    public string? Code { get; init; }

    /// <summary>
    /// Gets the component that produced the diagnostic.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Gets additional editor behavior associated with the finding.
    /// </summary>
    public IReadOnlyList<DiagnosticTag>? Tags { get; init; }

    /// <summary>
    /// Gets the localized diagnostic message.
    /// </summary>
    public required string Message { get; init; }
}
