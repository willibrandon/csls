namespace Csls.Cli.Worker;

/// <summary>
/// Contains one bounded CLI result page and its continuation cursor.
/// </summary>
/// <typeparam name="T">The paginated result item type.</typeparam>
internal sealed class CliPage<T>
{
    /// <summary>
    /// Gets the items retained in this page.
    /// </summary>
    internal required IReadOnlyList<T> Items { get; init; }

    /// <summary>
    /// Gets the opaque cursor for the next page when more items remain.
    /// </summary>
    internal string? NextCursor { get; init; }
}
