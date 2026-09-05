using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Owns one session-local source descriptor and optional retrievable content.
/// </summary>
internal sealed class DebugSourceRegistration
{
    /// <summary>
    /// Gets the externally visible source identity.
    /// </summary>
    internal required DebugSourceInfo Info { get; init; }

    /// <summary>
    /// Gets or sets cached retrievable source content when the reference is positive.
    /// </summary>
    internal DebugSourceContent? Content { get; set; }

    /// <summary>
    /// Gets the Source Link URI used to retrieve uncached content.
    /// </summary>
    internal Uri? SourceLinkUri { get; init; }
}
