using Csls.Debugger;

namespace Csls.DebugAdapter;

/// <summary>
/// Carries validated DAP launch mode and protocol-neutral process options.
/// </summary>
internal sealed class DapLaunchConfiguration
{
    /// <summary>
    /// Gets whether the target runs without managed debugging.
    /// </summary>
    internal required bool NoDebug { get; init; }

    /// <summary>
    /// Gets the concrete target invocation.
    /// </summary>
    internal required DebuggeeLaunchOptions Options { get; init; }
}
