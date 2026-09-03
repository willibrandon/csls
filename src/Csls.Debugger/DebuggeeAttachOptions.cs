using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Describes one managed debugger attachment and its runtime policy.
/// </summary>
public sealed class DebuggeeAttachOptions
{
    /// <summary>
    /// Gets the positive operating-system process identifier.
    /// </summary>
    public required int ProcessId { get; init; }

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
    /// Gets whether source stepping excludes non-user managed code.
    /// </summary>
    public bool JustMyCode { get; init; } = true;

    /// <summary>
    /// Gets whether source stepping skips managed properties and operators.
    /// </summary>
    public bool EnableStepFiltering { get; init; } = true;
}
