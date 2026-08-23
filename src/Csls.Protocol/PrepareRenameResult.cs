namespace Csls.Protocol;

/// <summary>
/// Describes the editable source range and current symbol name for rename.
/// </summary>
public sealed record PrepareRenameResult
{
    /// <summary>
    /// Gets the source range that identifies the rename target.
    /// </summary>
    public required Range Range { get; init; }

    /// <summary>
    /// Gets the current identifier shown in the rename input.
    /// </summary>
    public required string Placeholder { get; init; }
}
