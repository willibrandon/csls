namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects one running CoreCLR process for debugger attachment.
/// </summary>
/// <param name="ProcessId">The positive operating-system process identifier.</param>
public sealed record DebugAttachRequest(int ProcessId)
{
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
    /// Gets whether source stepping excludes non-user managed code.
    /// </summary>
    public bool JustMyCode { get; init; } = true;

    /// <summary>
    /// Gets whether source stepping skips managed properties and operators.
    /// </summary>
    public bool EnableStepFiltering { get; init; } = true;
}
