namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one generation-bound debugger expression completion.
/// </summary>
/// <param name="Label">The concise candidate label.</param>
/// <param name="Text">The source text inserted for the candidate.</param>
/// <param name="Detail">The runtime-backed candidate detail.</param>
/// <param name="Kind">The candidate category.</param>
/// <param name="ReplacementStart">The zero-based UTF-16 replacement start on the selected line.</param>
/// <param name="ReplacementLength">The UTF-16 replacement length on the selected line.</param>
public sealed record DebugCompletionInfo(
    string Label,
    string Text,
    string Detail,
    DebugCompletionItemKind Kind,
    int ReplacementStart,
    int ReplacementLength);
