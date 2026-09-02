namespace Csls.App;

/// <summary>
/// Describes one verified Microsoft .NET debugger distribution.
/// </summary>
internal sealed class DebuggerPackage
{
    /// <summary>
    /// Initializes a debugger distribution descriptor.
    /// </summary>
    /// <param name="identifier">The stable platform and content identifier.</param>
    /// <param name="source">The official Microsoft package address.</param>
    /// <param name="sha256">The expected package SHA-256 digest.</param>
    /// <param name="executableName">The debugger executable within the archive.</param>
    internal DebuggerPackage(
        string identifier,
        Uri source,
        string sha256,
        string executableName)
    {
        Identifier = identifier;
        Source = source;
        Sha256 = sha256;
        ExecutableName = executableName;
    }

    /// <summary>
    /// Gets the debugger executable within the archive.
    /// </summary>
    internal string ExecutableName { get; }

    /// <summary>
    /// Gets the stable platform and content identifier.
    /// </summary>
    internal string Identifier { get; }

    /// <summary>
    /// Gets the expected package SHA-256 digest.
    /// </summary>
    internal string Sha256 { get; }

    /// <summary>
    /// Gets the official Microsoft package address.
    /// </summary>
    internal Uri Source { get; }
}
