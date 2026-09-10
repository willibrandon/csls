namespace Csls.Debugger.Contracts;

/// <summary>
/// Contains one bounded page from the retained debugger-output stream.
/// </summary>
/// <param name="Entries">The ordered retained entries after the cursor.</param>
/// <param name="NextSequence">The cursor to use for the next page.</param>
/// <param name="FirstRetainedSequence">The oldest sequence still retained.</param>
/// <param name="DroppedBeforeStart">Entries lost between the cursor and retained window.</param>
/// <param name="HasMore">Whether another retained entry follows this page.</param>
public sealed record DebugOutputPage(
    IReadOnlyList<DebugOutputEntry> Entries,
    long NextSequence,
    long FirstRetainedSequence,
    long DroppedBeforeStart,
    bool HasMore);
