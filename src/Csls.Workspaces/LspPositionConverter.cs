using Csls.Protocol;
using Microsoft.CodeAnalysis.Text;

namespace Csls.Workspaces;

/// <summary>
/// Converts Language Server Protocol positions to bounded Roslyn text offsets.
/// </summary>
internal static class LspPositionConverter
{
    /// <summary>
    /// Converts a position while clamping values beyond the current document boundary.
    /// </summary>
    /// <param name="text">The current immutable document text.</param>
    /// <param name="position">The nonnegative LSP position.</param>
    /// <returns>The corresponding bounded UTF-16 text offset.</returns>
    internal static int GetOffset(SourceText text, Position position)
    {
        int lineIndex = Math.Min(position.Line, text.Lines.Count - 1);
        TextLine line = text.Lines[lineIndex];
        return line.Start + Math.Min(position.Character, line.Span.Length);
    }
}
