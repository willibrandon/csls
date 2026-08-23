namespace Csls.Protocol;

/// <summary>
/// Describes diagnostics and action categories requested by an editor.
/// </summary>
public sealed record CodeActionContext
{
    /// <summary>
    /// Gets diagnostics intersecting the requested source range.
    /// </summary>
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    /// <summary>
    /// Gets the optional action categories requested by the editor.
    /// </summary>
    public IReadOnlyList<string>? Only { get; init; }
}
