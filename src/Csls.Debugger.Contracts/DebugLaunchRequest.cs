namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one debugger-owned managed target launch.
/// </summary>
public sealed class DebugLaunchRequest
{
    /// <summary>
    /// Gets the absolute managed executable or assembly path.
    /// </summary>
    public required string Program { get; init; }

    /// <summary>
    /// Gets the absolute target working directory.
    /// </summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// Gets the ordered target arguments.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Gets target environment additions and removals.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the optional host used to run a managed assembly.
    /// </summary>
    public string? RuntimeHostPath { get; init; }
}
