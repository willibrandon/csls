namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one managed thread visible at a debugger stop.
/// </summary>
/// <param name="Id">The session-local thread identifier.</param>
/// <param name="Name">The best available display name.</param>
public sealed record DebugThreadInfo(int Id, string Name);
