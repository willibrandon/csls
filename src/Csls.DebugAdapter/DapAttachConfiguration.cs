using Csls.Debugger;

namespace Csls.DebugAdapter;

/// <summary>
/// Contains validated process and runtime options for one DAP attach request.
/// </summary>
internal sealed class DapAttachConfiguration
{
    /// <summary>
    /// Gets the protocol-neutral target and runtime options.
    /// </summary>
    internal required DebuggeeAttachOptions Options { get; init; }
}
