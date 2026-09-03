using Csls.Debugger.Contracts;

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
    /// Gets whether the debugger requests unoptimized JIT code for symbol-bearing modules.
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
