namespace Csls.Protocol;

/// <summary>
/// Replaces one half-open UTF-16 document range with new text.
/// </summary>
public sealed record TextEdit
{
    /// <summary>
    /// Gets the replaced document range.
    /// </summary>
    public required Range Range { get; init; }

    /// <summary>
    /// Gets the replacement text.
    /// </summary>
    public required string NewText { get; init; }
}
