namespace Csls.Debugger.Contracts;

/// <summary>
/// Identifies a debugger-owned target output stream.
/// </summary>
public enum DebugOutputCategory
{
    /// <summary>
    /// Standard output from the target process.
    /// </summary>
    StandardOutput,

    /// <summary>
    /// Standard error from the target process.
    /// </summary>
    StandardError
}
