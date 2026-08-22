using Microsoft.CodeAnalysis.QuickInfo;
using System.Text;

namespace Csls.Workspaces;

/// <summary>
/// Converts Roslyn quick-info sections into stable LSP Markdown content.
/// </summary>
internal static class QuickInfoMarkdownFormatter
{
    /// <summary>
    /// Preserves the signature, documentation, and auxiliary Roslyn quick-info sections.
    /// </summary>
    /// <param name="quickInfo">The Roslyn quick-info result.</param>
    /// <returns>Markdown suitable for an LSP hover response.</returns>
    internal static string Format(QuickInfoItem quickInfo)
    {
        ArgumentNullException.ThrowIfNull(quickInfo);
        var content = new StringBuilder();
        foreach (QuickInfoSection section in quickInfo.Sections)
        {
            string text = section.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (content.Length > 0)
            {
                content.Append("\n\n");
            }

            if (string.Equals(
                section.Kind,
                QuickInfoSectionKinds.Description,
                StringComparison.Ordinal))
            {
                content.Append("```csharp\n");
                content.Append(text);
                content.Append("\n```");
                continue;
            }

            string? heading = GetHeading(section.Kind);
            if (heading is not null)
            {
                content.Append("**");
                content.Append(heading);
                content.Append("**\n\n");
            }

            content.Append(text);
        }

        return content.ToString();
    }

    private static string? GetHeading(string sectionKind) => sectionKind switch
    {
        QuickInfoSectionKinds.RemarksDocumentationComments => "Remarks",
        QuickInfoSectionKinds.ReturnsDocumentationComments => "Returns",
        QuickInfoSectionKinds.ValueDocumentationComments => "Value",
        QuickInfoSectionKinds.TypeParameters => "Type parameters",
        QuickInfoSectionKinds.Usage => "Usage",
        QuickInfoSectionKinds.Exception => "Exceptions",
        QuickInfoSectionKinds.Captures => "Captures",
        _ => null
    };
}
