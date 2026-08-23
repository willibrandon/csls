namespace Csls.Protocol;

/// <summary>
/// Returns a bounded ordered set of completion candidates.
/// </summary>
public sealed record CompletionList
{
    /// <summary>
    /// Gets whether the client should request completion again after further filtering.
    /// </summary>
    public bool IsIncomplete { get; init; }

    /// <summary>
    /// Gets the ordered completion candidates.
    /// </summary>
    public required IReadOnlyList<CompletionItem> Items { get; init; }
}
