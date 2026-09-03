namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects one running CoreCLR process for debugger attachment.
/// </summary>
/// <param name="ProcessId">The positive operating-system process identifier.</param>
public sealed record DebugAttachRequest(int ProcessId);
