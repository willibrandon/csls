using Csls.Debugger;

namespace Csls.DebugAdapter;

/// <summary>
/// Contains validated target and runtime options for live process attachment.
/// </summary>
internal sealed class DapProcessAttachConfiguration : DapAttachConfiguration
{
    /// <summary>
    /// Gets the protocol-neutral process attachment options.
    /// </summary>
    internal required DebuggeeAttachOptions Options { get; init; }
}
