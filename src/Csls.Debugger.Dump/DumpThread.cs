using Microsoft.Diagnostics.Runtime;

namespace Csls.Debugger.Dump;

/// <summary>
/// Binds a dump runtime thread to its stable session-local identifier.
/// </summary>
/// <param name="Id">The stable session-local thread identifier.</param>
/// <param name="Thread">The ClrMD thread projection.</param>
internal sealed record DumpThread(int Id, ClrThread Thread);
