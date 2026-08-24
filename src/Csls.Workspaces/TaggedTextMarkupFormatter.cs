using Csls.Protocol;
using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using System.Text;

namespace Csls.Workspaces;

/// <summary>
/// Converts Roslyn tagged text into an LSP documentation format supported by the client.
/// </summary>
internal static class TaggedTextMarkupFormatter
{
    private const string CodeBlockEndTag = "CodeBlockEnd";
    private const string CodeBlockStartTag = "CodeBlockStart";

    /// <summary>
    /// Appends nonduplicated supplemental documentation to primary markup.
    /// </summary>
    /// <param name="primary">The primary documentation content.</param>
    /// <param name="supplemental">The optional supplemental documentation content.</param>
    /// <returns>The combined documentation content.</returns>
    internal static MarkupContent Combine(
        MarkupContent primary,
        MarkupContent? supplemental)
    {
        ArgumentNullException.ThrowIfNull(primary);
        if (supplemental is null ||
            string.IsNullOrWhiteSpace(supplemental.Value) ||
            primary.Value.Contains(supplemental.Value, StringComparison.Ordinal))
        {
            return primary;
        }

        return primary with
        {
            Value = string.Concat(
                primary.Value.TrimEnd(),
                Environment.NewLine,
                Environment.NewLine,
                supplemental.Value.TrimStart())
        };
    }

    /// <summary>
    /// Formats ordered Roslyn tagged text as Markdown or plain text.
    /// </summary>
    /// <param name="parts">The ordered Roslyn tagged text.</param>
    /// <param name="supportsMarkdown">Whether the receiving client accepts Markdown.</param>
    /// <returns>The formatted LSP markup content.</returns>
    internal static MarkupContent Format(
        ImmutableArray<TaggedText> parts,
        bool supportsMarkdown)
    {
        if (!supportsMarkdown)
        {
            var plainText = new StringBuilder();
            foreach (TaggedText part in parts)
            {
                plainText.Append(part.Text);
            }

            return new MarkupContent
            {
                Kind = "plaintext",
                Value = plainText.ToString().Trim()
            };
        }

        var content = new StringBuilder();
        bool inCodeBlock = false;
        foreach (TaggedText part in parts)
        {
            switch (part.Tag)
            {
                case CodeBlockStartTag:
                    if (content.Length > 0 && content[^1] != '\n')
                    {
                        content.AppendLine();
                    }

                    content.AppendLine("```csharp");
                    content.Append(part.Text);
                    inCodeBlock = true;
                    break;
                case CodeBlockEndTag:
                    if (content.Length > 0 && content[^1] != '\n')
                    {
                        content.AppendLine();
                    }

                    content.AppendLine("```");
                    content.Append(part.Text);
                    inCodeBlock = false;
                    break;
                case TextTags.LineBreak:
                    if (inCodeBlock)
                    {
                        content.AppendLine();
                    }
                    else
                    {
                        content.Append("  ");
                        content.AppendLine();
                    }

                    break;
                default:
                    if (inCodeBlock)
                    {
                        content.Append(part.Text);
                    }
                    else
                    {
                        AppendEscapedMarkdown(content, part.Text);
                    }

                    break;
            }
        }

        return new MarkupContent
        {
            Kind = "markdown",
            Value = content.ToString().Trim()
        };
    }

    private static void AppendEscapedMarkdown(StringBuilder destination, string value)
    {
        foreach (char character in value)
        {
            if (character is
                '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '(' or ')' or
                '#' or '+' or '-' or '.' or '!' or '<' or '>' or '|')
            {
                destination.Append('\\');
            }

            destination.Append(character);
        }
    }
}
