namespace Csls.Debugger.Terminal;

/// <summary>
/// Describes one running managed process shown in the interactive debugger.
/// </summary>
/// <param name="ProcessId">The positive target process identifier.</param>
public sealed record DebuggerTerminalAttachOptions(int ProcessId)
{
    /// <summary>
    /// Gets build-time source prefixes mapped to local source prefixes.
    /// </summary>
    public IReadOnlyDictionary<string, string> SourceFileMap { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
