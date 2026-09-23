using Csls.Debugger.Contracts;

namespace Csls.DebugAdapter;

/// <summary>
/// Contains a validated dump path and its managed-runtime selector.
/// </summary>
internal sealed class DapDumpAttachConfiguration : DapAttachConfiguration
{
    /// <summary>
    /// Gets the protocol-neutral dump activation options.
    /// </summary>
    internal required DebugDumpOpenRequest Options { get; init; }
}
