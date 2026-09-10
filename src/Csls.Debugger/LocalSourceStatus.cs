namespace Csls.Debugger;

/// <summary>
/// Describes the readability and checksum identity of a bounded local source file.
/// </summary>
internal enum LocalSourceStatus
{
    /// <summary>
    /// Identifies a path that cannot provide bounded seekable source content.
    /// </summary>
    Unavailable,

    /// <summary>
    /// Identifies readable source whose symbols provide no supported checksum.
    /// </summary>
    Unverified,

    /// <summary>
    /// Identifies readable source that matches its managed PDB checksum.
    /// </summary>
    Verified,

    /// <summary>
    /// Identifies readable source that differs from its managed PDB checksum.
    /// </summary>
    Mismatch
}
