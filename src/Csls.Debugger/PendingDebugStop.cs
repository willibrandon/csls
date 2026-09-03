using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Carries a runtime stop that arrived before target-start publication completed.
/// </summary>
/// <param name="Reason">The protocol-neutral stop reason.</param>
/// <param name="ThreadId">The triggering managed thread identifier.</param>
/// <param name="Exception">The current managed exception when applicable.</param>
internal sealed record PendingDebugStop(
    string Reason,
    int ThreadId,
    DebugExceptionInfo? Exception);
