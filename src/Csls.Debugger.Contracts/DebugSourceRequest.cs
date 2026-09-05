namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects retrievable source content by its session-local reference.
/// </summary>
/// <param name="SourceReference">The positive session-local source reference.</param>
public sealed record DebugSourceRequest(int SourceReference);
