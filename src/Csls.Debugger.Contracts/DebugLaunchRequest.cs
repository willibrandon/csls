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

    /// <summary>
    /// Gets build-time source prefixes mapped to local editor paths.
    /// </summary>
    public IReadOnlyDictionary<string, string> SourceFileMap { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets Source Link URL patterns mapped to enabled states.
    /// </summary>
    public IReadOnlyDictionary<string, bool> SourceLinkOptions { get; init; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets trusted symbol search paths, servers, and cache configuration.
    /// </summary>
    public DebugSymbolOptions SymbolOptions { get; init; } = new();

    /// <summary>
    /// Gets whether the debugger requests unoptimized JIT code for loaded managed modules.
    /// </summary>
    public bool SuppressJitOptimizations { get; init; }

    /// <summary>
    /// Gets whether source stepping excludes non-user managed code.
    /// </summary>
    public bool JustMyCode { get; init; } = true;

    /// <summary>
    /// Gets whether source stepping skips managed properties and operators.
    /// </summary>
    public bool EnableStepFiltering { get; init; } = true;
}
