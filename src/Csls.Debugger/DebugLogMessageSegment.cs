namespace Csls.Debugger;

/// <summary>
/// Represents literal text or one expression in a parsed logpoint message.
/// </summary>
/// <param name="Text">The literal text or source expression.</param>
/// <param name="IsExpression">Whether the text must be evaluated.</param>
internal sealed record DebugLogMessageSegment(string Text, bool IsExpression);
