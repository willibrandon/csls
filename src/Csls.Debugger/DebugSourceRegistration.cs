using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Owns one session-local source descriptor and optional retrievable content.
/// </summary>
internal sealed class DebugSourceRegistration
{
    /// <summary>
    /// Gets or sets the current externally visible source identity.
    /// </summary>
    internal required DebugSourceInfo Info { get; set; }

    /// <summary>
    /// Gets the immutable symbol document used to revalidate local files and canonical retrieved content.
    /// </summary>
    internal ManagedSymbolDocument? Document { get; init; }

    /// <summary>
    /// Gets or sets the stable reference reserved for canonical embedded or Source Link content.
    /// </summary>
    internal int SourceReference { get; set; }

    /// <summary>
    /// Gets or sets cached retrievable source content when the reference is positive.
    /// </summary>
    internal DebugSourceContent? Content { get; set; }

    /// <summary>
    /// Gets the Source Link URI used to retrieve uncached content.
    /// </summary>
    internal Uri? SourceLinkUri => Document?.SourceLinkUri;
}
