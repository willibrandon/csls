namespace Csls.Debugger;

/// <summary>
/// Describes a concrete program invocation owned by a debugger session.
/// </summary>
public sealed class DebuggeeLaunchOptions
{
    /// <summary>
    /// Gets the absolute managed assembly or executable path.
    /// </summary>
    public required string Program { get; init; }

    /// <summary>
    /// Gets the absolute working directory.
    /// </summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// Gets the target argument sequence.
    /// </summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>
    /// Gets environment additions and removals for the target.
    /// </summary>
    public required IReadOnlyDictionary<string, string?> Environment { get; init; }

    /// <summary>
    /// Gets the host used to execute managed assemblies.
    /// </summary>
    public string? RuntimeHostPath { get; init; }
}
