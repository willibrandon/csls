using System.Text.Json;

namespace Csls.Protocol;

/// <summary>
/// Describes one editor command and its JSON-compatible arguments.
/// </summary>
public sealed record LspCommand
{
    /// <summary>
    /// Gets the human-readable command title displayed by the editor.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the command identifier interpreted by the connected editor.
    /// </summary>
    public required string Command { get; init; }

    /// <summary>
    /// Gets the ordered command arguments when the command requires them.
    /// </summary>
    public IReadOnlyList<JsonElement>? Arguments { get; init; }
}
