namespace Csls.Mcp.Worker;

/// <summary>
/// Returns a bounded page of generation-bound debugger source text.
/// </summary>
/// <param name="DebugSession">The exact debugger-session identifier.</param>
/// <param name="StopGeneration">The inspected stop generation.</param>
/// <param name="Content">The requested source-text page.</param>
/// <param name="MimeType">The source media type.</param>
/// <param name="Start">The zero-based character offset.</param>
/// <param name="TotalCharacters">The complete source length.</param>
/// <param name="NextStart">The next offset, or null when the page is complete.</param>
internal sealed record McpDebugSourceResult(
    string DebugSession,
    long StopGeneration,
    string Content,
    string MimeType,
    int Start,
    int TotalCharacters,
    int? NextStart);
