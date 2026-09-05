namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects the managed thread that caused the current exception stop.
/// </summary>
/// <param name="ThreadId">The managed thread identifier.</param>
public sealed record DebugExceptionInfoRequest(int ThreadId);
