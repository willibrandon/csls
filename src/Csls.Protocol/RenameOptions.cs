namespace Csls.Protocol;

/// <summary>
/// Advertises server-side rename validation before an edit is requested.
/// </summary>
public sealed record RenameOptions
{
    /// <summary>
    /// Gets whether prepare-rename requests are supported.
    /// </summary>
    public bool PrepareProvider { get; init; }
}
