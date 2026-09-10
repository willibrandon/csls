namespace Csls.Debugger.Contracts;

/// <summary>
/// Carries source text resolved through a session-local source reference.
/// </summary>
/// <param name="Content">The complete source text.</param>
/// <param name="MimeType">The source media type.</param>
public sealed record DebugSourceContent(string Content, string MimeType);
