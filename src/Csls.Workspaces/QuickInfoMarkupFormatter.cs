using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.QuickInfo;
using System.Collections.Immutable;

namespace Csls.Workspaces;

/// <summary>
/// Converts Roslyn quick-info sections into stable LSP documentation content.
/// </summary>
internal static class QuickInfoMarkupFormatter
{
    /// <summary>
    /// Preserves the signature, documentation, and auxiliary Roslyn quick-info sections.
    /// </summary>
    /// <param name="quickInfo">The Roslyn quick-info result.</param>
    /// <param name="supportsMarkdown">Whether the receiving client accepts Markdown.</param>
    /// <returns>Documentation suitable for an LSP hover response.</returns>
    internal static MarkupContent Format(QuickInfoItem quickInfo, bool supportsMarkdown)
    {
        ArgumentNullException.ThrowIfNull(quickInfo);
        ImmutableArray<TaggedText>.Builder parts = ImmutableArray.CreateBuilder<TaggedText>();
        foreach (QuickInfoSection section in quickInfo.Sections)
        {
            if (section.TaggedParts.IsDefaultOrEmpty)
            {
                continue;
            }

            if (parts.Count > 0)
            {
                parts.Add(new TaggedText(TextTags.LineBreak, Environment.NewLine));
            }

            parts.AddRange(section.TaggedParts);
        }

        return TaggedTextMarkupFormatter.Format(parts.ToImmutable(), supportsMarkdown);
    }
}
