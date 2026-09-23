using System.Text;

namespace Csls.Support;

/// <summary>
/// Applies consistent whitespace and Markdown table formatting to generated documentation.
/// </summary>
internal static class DocumentationText
{
    /// <summary>
    /// Collapses whitespace runs while preserving the text between them.
    /// </summary>
    /// <param name="value">The source documentation text.</param>
    /// <returns>The text without leading, trailing, or repeated whitespace.</returns>
    internal static string NormalizeWhitespace(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length != 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }

            result.Append(character);
        }

        return result.ToString();
    }

    /// <summary>
    /// Normalizes and escapes one Markdown table cell without changing its displayed text.
    /// </summary>
    /// <param name="value">The optional unescaped table-cell text.</param>
    /// <returns>The normalized escaped text, or an empty string for missing content.</returns>
    internal static string EscapeTableText(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : NormalizeWhitespace(value)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);

    /// <summary>
    /// Removes trailing whitespace and terminates generated content with one newline.
    /// </summary>
    /// <param name="value">The generated documentation content.</param>
    /// <returns>The content ending in exactly one line-feed character.</returns>
    internal static string EnsureFinalNewLine(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.TrimEnd() + '\n';
    }
}
