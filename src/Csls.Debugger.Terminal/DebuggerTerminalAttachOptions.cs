using Csls.Debugger.Contracts;

namespace Csls.Debugger.Terminal;

/// <summary>
/// Describes one running managed process shown in the interactive debugger.
/// </summary>
/// <param name="ProcessId">The positive target process identifier.</param>
public sealed record DebuggerTerminalAttachOptions(int ProcessId)
{
    /// <summary>
    /// Gets whether local source must match its debug symbols.
    /// </summary>
    public bool RequireExactSource { get; init; } = true;

    /// <summary>
    /// Gets the session's managed value presentation options.
    /// </summary>
    public DebugExpressionEvaluationOptions ExpressionEvaluationOptions { get; init; } = new();

    /// <summary>
    /// Gets build-time source prefixes mapped to local source prefixes.
    /// </summary>
    public IReadOnlyDictionary<string, string> SourceFileMap { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
