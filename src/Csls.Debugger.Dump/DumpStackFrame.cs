using Microsoft.Diagnostics.Runtime;

namespace Csls.Debugger.Dump;

/// <summary>
/// Binds a dump stack frame to its stable session-local identifier.
/// </summary>
/// <param name="Id">The stable session-local frame identifier.</param>
/// <param name="Frame">The ClrMD frame projection.</param>
internal sealed record DumpStackFrame(int Id, ClrStackFrame Frame);
