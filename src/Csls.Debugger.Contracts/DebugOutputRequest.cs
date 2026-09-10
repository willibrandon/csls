namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects a bounded target-output page after a stable sequence cursor.
/// </summary>
/// <param name="AfterSequence">The last previously observed sequence, or zero.</param>
/// <param name="Count">The maximum number of entries to return.</param>
public sealed record DebugOutputRequest(long AfterSequence, int Count);
