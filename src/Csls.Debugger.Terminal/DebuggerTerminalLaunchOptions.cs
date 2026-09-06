namespace Csls.Debugger.Terminal;

/// <summary>
/// Describes one managed launch shown in the interactive debugger.
/// </summary>
public sealed class DebuggerTerminalLaunchOptions
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
    /// Gets the source document containing the initial breakpoint.
    /// </summary>
    public string? SourcePath { get; init; }

    /// <summary>
    /// Gets the one-based initial breakpoint line.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// Gets whether launch stops at the first executable entry-point statement.
    /// </summary>
    public bool StopAtEntry { get; init; }

    /// <summary>
    /// Gets whether local source must match its debug symbols.
    /// </summary>
    public bool RequireExactSource { get; init; } = true;

    /// <summary>
    /// Gets the ordered target arguments.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Gets the optional UTF-8 environment file resolved relative to the target working directory.
    /// </summary>
    public string? EnvironmentFilePath { get; init; }

    /// <summary>
    /// Gets the optional runtime host used for a managed assembly.
    /// </summary>
    public string? RuntimeHostPath { get; init; }

    /// <summary>
    /// Gets build-time source prefixes mapped to local source prefixes.
    /// </summary>
    public IReadOnlyDictionary<string, string> SourceFileMap { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
