namespace Csls.Debugger.Contracts;

/// <summary>
/// Identifies a debugger or target output channel.
/// </summary>
public enum DebugOutputCategory
{
    /// <summary>
    /// Debugger console output such as an evaluated logpoint message.
    /// </summary>
    Console,

    /// <summary>
    /// Standard output from the target process.
    /// </summary>
    StandardOutput,

    /// <summary>
    /// Standard error from the target process.
    /// </summary>
    StandardError
}
