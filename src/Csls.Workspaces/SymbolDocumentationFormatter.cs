using Csls.Protocol;
using Microsoft.CodeAnalysis;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Csls.Workspaces;

/// <summary>
/// Resolves and formats compiler XML documentation for Roslyn symbols.
/// </summary>
internal static class SymbolDocumentationFormatter
{
    private const long MaximumDocumentationCharacters = 1_000_000;
    private static readonly ConditionalWeakTable<
        Compilation,
        ConcurrentDictionary<
            string,
            (MarkupContent? Documentation,
            MarkupContent? SupplementalDocumentation,
            IReadOnlyDictionary<string, MarkupContent> Parameters)>> s_cache = [];

    /// <summary>
    /// Formats symbol and parameter documentation with inherited sections filled from its symbol graph.
    /// </summary>
    /// <param name="symbol">The Roslyn symbol.</param>
    /// <param name="compilation">The compilation used to resolve documentation identifiers.</param>
    /// <param name="supportsMarkdown">Whether the receiving client accepts Markdown.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The full, supplemental, and parameter documentation.</returns>
    internal static (
        MarkupContent? Documentation,
        MarkupContent? SupplementalDocumentation,
        IReadOnlyDictionary<string, MarkupContent> Parameters) FormatSymbol(
        ISymbol symbol,
        Compilation compilation,
        bool supportsMarkdown,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(compilation);
        ISymbol definition = symbol.OriginalDefinition;
        string symbolKey = definition.GetDocumentationCommentId()
            ?? definition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string cacheKey = string.Concat(supportsMarkdown ? "m:" : "p:", symbolKey);
        ConcurrentDictionary<
            string,
            (MarkupContent? Documentation,
            MarkupContent? SupplementalDocumentation,
            IReadOnlyDictionary<string, MarkupContent> Parameters)> cache =
            s_cache.GetOrCreateValue(compilation);
        return cache.GetOrAdd(
            cacheKey,
            _ => FormatSymbolCore(
                definition,
                compilation,
                supportsMarkdown,
                cancellationToken));
    }

    private static (
        MarkupContent? Documentation,
        MarkupContent? SupplementalDocumentation,
        IReadOnlyDictionary<string, MarkupContent> Parameters) FormatSymbolCore(
        ISymbol symbol,
        Compilation compilation,
        bool supportsMarkdown,
        CancellationToken cancellationToken)
    {
        XElement? root = ResolveDocumentationRoot(
            symbol,
            compilation,
            new HashSet<ISymbol>(SymbolEqualityComparer.Default),
            cancellationToken);
        if (root is null)
        {
            return (
                null,
                null,
                new Dictionary<string, MarkupContent>(StringComparer.Ordinal));
        }

        var content = new StringBuilder();
        AppendElements(content, root.Elements("summary"), label: null, supportsMarkdown);
        AppendElements(content, root.Elements("remarks"), "Remarks", supportsMarkdown);
        AppendElements(content, root.Elements("returns"), "Returns", supportsMarkdown);
        AppendElements(content, root.Elements("value"), "Value", supportsMarkdown);
        AppendNamedElements(
            content,
            root.Elements("typeparam"),
            "Type parameters",
            "name",
            supportsMarkdown);
        AppendNamedElements(
            content,
            root.Elements("exception"),
            "Exceptions",
            "cref",
            supportsMarkdown);
        AppendElements(content, root.Elements("example"), "Example", supportsMarkdown);
        AppendReferences(content, root.Elements("seealso"), supportsMarkdown);
        string value = content.ToString().Trim();
        MarkupContent? documentation = value.Length == 0
            ? null
            : new MarkupContent
            {
                Kind = supportsMarkdown ? "markdown" : "plaintext",
                Value = value
            };
        var supplementalContent = new StringBuilder();
        AppendElements(
            supplementalContent,
            root.Elements("example"),
            "Example",
            supportsMarkdown);
        AppendReferences(
            supplementalContent,
            root.Elements("seealso"),
            supportsMarkdown);
        string supplementalValue = supplementalContent.ToString().Trim();
        MarkupContent? supplementalDocumentation = supplementalValue.Length == 0
            ? null
            : new MarkupContent
            {
                Kind = supportsMarkdown ? "markdown" : "plaintext",
                Value = supplementalValue
            };
        var parameters = new Dictionary<string, MarkupContent>(StringComparer.Ordinal);
        foreach (XElement element in root.Elements("param"))
        {
            string? name = element.Attribute("name")?.Value;
            string parameterValue = FormatNodes(element.Nodes(), supportsMarkdown).Trim();
            if (!string.IsNullOrWhiteSpace(name) && parameterValue.Length > 0)
            {
                parameters.TryAdd(
                    name,
                    new MarkupContent
                    {
                        Kind = supportsMarkdown ? "markdown" : "plaintext",
                        Value = parameterValue
                    });
            }
        }

        return (documentation, supplementalDocumentation, parameters);
    }

    private static XElement? ResolveDocumentationRoot(
        ISymbol symbol,
        Compilation compilation,
        HashSet<ISymbol> visited,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ISymbol definition = symbol.OriginalDefinition;
        if (!visited.Add(definition))
        {
            return null;
        }

        XElement? ownRoot = ParseDocumentation(
            definition.GetDocumentationCommentXml(
                expandIncludes: true,
                cancellationToken: cancellationToken));
        ISymbol? inheritedSymbol = FindInheritedSymbol(
            definition,
            ownRoot,
            compilation);
        XElement? inheritedRoot = inheritedSymbol is null
            ? null
            : ResolveDocumentationRoot(
                inheritedSymbol,
                compilation,
                visited,
                cancellationToken);
        if (ownRoot is null)
        {
            return inheritedRoot;
        }

        if (inheritedRoot is null)
        {
            return ownRoot;
        }

        var merged = new XElement("doc");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement element in ownRoot.Elements())
        {
            if (string.Equals(element.Name.LocalName, "inheritdoc", StringComparison.Ordinal))
            {
                continue;
            }

            merged.Add(new XElement(element));
            keys.Add(GetSectionKey(element));
        }

        foreach (XElement element in inheritedRoot.Elements())
        {
            if (keys.Add(GetSectionKey(element)))
            {
                merged.Add(new XElement(element));
            }
        }

        return merged;
    }

    private static ISymbol? FindInheritedSymbol(
        ISymbol symbol,
        XElement? root,
        Compilation compilation)
    {
        string? cref = root?
            .DescendantsAndSelf("inheritdoc")
            .Select(static element => element.Attribute("cref")?.Value)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        if (cref is not null)
        {
            ISymbol? resolved = DocumentationCommentId.GetFirstSymbolForDeclarationId(
                cref,
                compilation);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        if (symbol is not IMethodSymbol method)
        {
            return null;
        }

        if (method.OverriddenMethod is not null)
        {
            return method.OverriddenMethod;
        }

        if (!method.ExplicitInterfaceImplementations.IsDefaultOrEmpty)
        {
            return method.ExplicitInterfaceImplementations[0];
        }

        INamedTypeSymbol containingType = method.ContainingType;
        foreach (INamedTypeSymbol interfaceType in containingType.AllInterfaces)
        {
            foreach (IMethodSymbol interfaceMethod in interfaceType
                .GetMembers(method.Name)
                .OfType<IMethodSymbol>())
            {
                ISymbol? implementation = containingType.FindImplementationForInterfaceMember(
                    interfaceMethod);
                if (implementation is not null &&
                    (SymbolEqualityComparer.Default.Equals(implementation, method) ||
                    SymbolEqualityComparer.Default.Equals(
                        implementation.OriginalDefinition,
                        method.OriginalDefinition)))
                {
                    return interfaceMethod;
                }
            }
        }

        return null;
    }

    private static XElement? ParseDocumentation(string? documentation)
    {
        if (string.IsNullOrWhiteSpace(documentation))
        {
            return null;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            MaxCharactersInDocument = MaximumDocumentationCharacters,
            XmlResolver = null
        };
        try
        {
            using var textReader = new StringReader($"<doc>{documentation}</doc>");
            using var reader = XmlReader.Create(textReader, settings);
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            XElement root = document.Root
                ?? throw new InvalidDataException("The documentation XML has no root element.");
            XElement[] elements = [.. root.Elements()];
            return elements is [XElement single] &&
                single.Name.LocalName is "member" or "doc"
                    ? single
                    : root;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string GetSectionKey(XElement element)
    {
        string qualifier = element.Name.LocalName switch
        {
            "param" or "typeparam" => element.Attribute("name")?.Value ?? string.Empty,
            "exception" or "seealso" => element.Attribute("cref")?.Value ?? string.Empty,
            _ => string.Empty
        };
        return string.Concat(element.Name.LocalName, ":", qualifier);
    }

    private static void AppendElements(
        StringBuilder destination,
        IEnumerable<XElement> elements,
        string? label,
        bool supportsMarkdown)
    {
        foreach (XElement element in elements)
        {
            string value = FormatNodes(element.Nodes(), supportsMarkdown).Trim();
            if (value.Length == 0)
            {
                continue;
            }

            AppendSectionSeparator(destination);
            AppendLabel(destination, label, supportsMarkdown);
            destination.Append(value);
        }
    }

    private static void AppendNamedElements(
        StringBuilder destination,
        IEnumerable<XElement> elements,
        string label,
        string attributeName,
        bool supportsMarkdown)
    {
        string[] entries =
        [
            .. elements.Select(element =>
            {
                string name = element.Attribute(attributeName)?.Value ?? "unspecified";
                if (string.Equals(attributeName, "cref", StringComparison.Ordinal))
                {
                    name = FormatCref(name);
                }

                string value = FormatNodes(element.Nodes(), supportsMarkdown).Trim();
                string formattedName = supportsMarkdown ? FormatCodeSpan(name) : name;
                return value.Length == 0
                    ? formattedName
                    : string.Concat(formattedName, ": ", value);
            })
        ];
        if (entries.Length == 0)
        {
            return;
        }

        AppendSectionSeparator(destination);
        AppendLabel(destination, label, supportsMarkdown);
        destination.AppendLine();
        foreach (string entry in entries)
        {
            destination.Append("- ");
            destination.AppendLine(entry);
        }

        destination.Length--;
    }

    private static void AppendReferences(
        StringBuilder destination,
        IEnumerable<XElement> elements,
        bool supportsMarkdown)
    {
        string[] references =
        [
            .. elements
                .Select(element => FormatReference(element, supportsMarkdown))
                .Where(static reference => reference.Length > 0)
        ];
        if (references.Length == 0)
        {
            return;
        }

        AppendSectionSeparator(destination);
        AppendLabel(destination, "See also", supportsMarkdown);
        destination.AppendLine();
        foreach (string reference in references)
        {
            destination.Append("- ");
            destination.AppendLine(reference);
        }

        destination.Length--;
    }

    private static string FormatNodes(IEnumerable<XNode> nodes, bool supportsMarkdown)
    {
        var content = new StringBuilder();
        foreach (XNode node in nodes)
        {
            AppendNode(content, node, supportsMarkdown);
        }

        return content.ToString();
    }

    private static void AppendNode(
        StringBuilder destination,
        XNode node,
        bool supportsMarkdown)
    {
        if (node is XText text)
        {
            AppendNormalizedText(destination, text.Value, supportsMarkdown);
            return;
        }

        if (node is not XElement element)
        {
            return;
        }

        switch (element.Name.LocalName)
        {
            case "see":
            case "seealso":
            case "a":
                destination.Append(FormatReference(element, supportsMarkdown));
                break;
            case "paramref":
            case "typeparamref":
            case "c":
                string code = element.Attribute("name")?.Value ?? element.Value.Trim();
                destination.Append(supportsMarkdown ? FormatCodeSpan(code) : code);
                break;
            case "code":
                AppendCodeBlock(destination, element.Value, supportsMarkdown);
                break;
            case "para":
            case "p":
                AppendParagraphBreak(destination);
                AppendNodes(destination, element.Nodes(), supportsMarkdown);
                AppendParagraphBreak(destination);
                break;
            case "br":
                destination.AppendLine();
                break;
            case "list":
                AppendList(destination, element, supportsMarkdown);
                break;
            case "b":
                AppendStyled(destination, element, "**", supportsMarkdown);
                break;
            case "i":
                AppendStyled(destination, element, "_", supportsMarkdown);
                break;
            case "u":
                AppendStyled(destination, element, "<u>", "</u>", supportsMarkdown);
                break;
            case "inheritdoc":
                break;
            default:
                AppendNodes(destination, element.Nodes(), supportsMarkdown);
                break;
        }
    }

    private static void AppendNodes(
        StringBuilder destination,
        IEnumerable<XNode> nodes,
        bool supportsMarkdown)
    {
        foreach (XNode node in nodes)
        {
            AppendNode(destination, node, supportsMarkdown);
        }
    }

    private static string FormatReference(XElement element, bool supportsMarkdown)
    {
        string label = element.Value.Trim();
        string? href = element.Attribute("href")?.Value;
        if (href is not null)
        {
            label = label.Length == 0 ? href : label;
            return supportsMarkdown && TryNormalizeLink(href, out string? normalizedLink)
                ? string.Concat("[", EscapeMarkdown(label), "](", normalizedLink, ")")
                : label;
        }

        string? value = element.Attribute("cref")?.Value;
        value = value is null
            ? element.Attribute("langword")?.Value
            : FormatCref(value);
        if (label.Length > 0)
        {
            value = label;
        }

        return value is null
            ? string.Empty
            : supportsMarkdown
                ? FormatCodeSpan(value)
                : value;
    }

    private static void AppendList(
        StringBuilder destination,
        XElement list,
        bool supportsMarkdown)
    {
        XElement[] items = [.. list.Elements("item")];
        if (items.Length == 0)
        {
            return;
        }

        AppendParagraphBreak(destination);
        string type = list.Attribute("type")?.Value ?? "bullet";
        if (supportsMarkdown && string.Equals(type, "table", StringComparison.Ordinal))
        {
            XElement? header = list.Element("listheader");
            string termHeader = FormatListPart(header, "term", supportsMarkdown, "Term");
            string descriptionHeader = FormatListPart(
                header,
                "description",
                supportsMarkdown,
                "Description");
            destination.Append("| ");
            destination.Append(EscapeTableCell(termHeader));
            destination.Append(" | ");
            destination.Append(EscapeTableCell(descriptionHeader));
            destination.AppendLine(" |");
            destination.AppendLine("| --- | --- |");
            foreach (XElement item in items)
            {
                destination.Append("| ");
                destination.Append(EscapeTableCell(
                    FormatListPart(item, "term", supportsMarkdown)));
                destination.Append(" | ");
                destination.Append(EscapeTableCell(
                    FormatListPart(item, "description", supportsMarkdown)));
                destination.AppendLine(" |");
            }
        }
        else
        {
            for (int index = 0; index < items.Length; index++)
            {
                string term = FormatListPart(items[index], "term", supportsMarkdown);
                string description = FormatListPart(
                    items[index],
                    "description",
                    supportsMarkdown);
                string content = term.Length > 0 && description.Length > 0
                    ? string.Concat(term, ": ", description)
                    : string.Concat(term, description);
                if (content.Length == 0)
                {
                    content = FormatNodes(items[index].Nodes(), supportsMarkdown).Trim();
                }

                destination.Append(
                    string.Equals(type, "number", StringComparison.Ordinal)
                        ? $"{index + 1}. "
                        : "- ");
                destination.AppendLine(content);
            }
        }

        while (destination.Length > 0 && destination[^1] == '\n')
        {
            destination.Length--;
        }

        AppendParagraphBreak(destination);
    }

    private static string FormatListPart(
        XElement? element,
        string partName,
        bool supportsMarkdown,
        string fallback = "")
    {
        XElement? part = element?.Element(partName);
        return part is null
            ? fallback
            : FormatNodes(part.Nodes(), supportsMarkdown).Trim();
    }

    private static void AppendCodeBlock(
        StringBuilder destination,
        string code,
        bool supportsMarkdown)
    {
        AppendParagraphBreak(destination);
        string value = TrimCodeIndentation(code);
        if (supportsMarkdown)
        {
            string fence = CreateCodeFence(value);
            destination.Append(fence);
            destination.AppendLine("csharp");
            destination.AppendLine(value);
            destination.Append(fence);
        }
        else
        {
            destination.Append(value);
        }

        AppendParagraphBreak(destination);
    }

    private static string TrimCodeIndentation(string code)
    {
        string[] lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int start = 0;
        while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start]))
        {
            start++;
        }

        int end = lines.Length;
        while (end > start && string.IsNullOrWhiteSpace(lines[end - 1]))
        {
            end--;
        }

        int indentation = lines
            .Skip(start)
            .Take(end - start)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Select(static line => line.Length - line.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();
        return string.Join(
            '\n',
            lines
                .Skip(start)
                .Take(end - start)
                .Select(line => line.Length >= indentation
                    ? line[indentation..].TrimEnd()
                    : string.Empty));
    }

    private static void AppendNormalizedText(
        StringBuilder destination,
        string value,
        bool supportsMarkdown)
    {
        bool pendingSpace = false;
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = destination.Length > 0 &&
                    destination[^1] is not '\n' and not ' ';
                continue;
            }

            if (pendingSpace)
            {
                destination.Append(' ');
                pendingSpace = false;
            }

            if (supportsMarkdown && IsMarkdownCharacter(character))
            {
                destination.Append('\\');
            }

            destination.Append(character);
        }
    }

    private static void AppendStyled(
        StringBuilder destination,
        XElement element,
        string marker,
        bool supportsMarkdown) =>
        AppendStyled(destination, element, marker, marker, supportsMarkdown);

    private static void AppendStyled(
        StringBuilder destination,
        XElement element,
        string startMarker,
        string endMarker,
        bool supportsMarkdown)
    {
        if (supportsMarkdown)
        {
            destination.Append(startMarker);
        }

        AppendNodes(destination, element.Nodes(), supportsMarkdown);
        if (supportsMarkdown)
        {
            destination.Append(endMarker);
        }
    }

    private static void AppendParagraphBreak(StringBuilder destination)
    {
        while (destination.Length > 0 && destination[^1] == ' ')
        {
            destination.Length--;
        }

        if (destination.Length == 0)
        {
            return;
        }

        if (destination[^1] != '\n')
        {
            destination.AppendLine();
        }

        if (destination.Length < 2 || destination[^2] != '\n')
        {
            destination.AppendLine();
        }
    }

    private static void AppendSectionSeparator(StringBuilder destination)
    {
        if (destination.Length > 0)
        {
            AppendParagraphBreak(destination);
        }
    }

    private static void AppendLabel(
        StringBuilder destination,
        string? label,
        bool supportsMarkdown)
    {
        if (label is null)
        {
            return;
        }

        if (supportsMarkdown)
        {
            destination.Append("**");
        }

        destination.Append(label);
        destination.Append(':');
        if (supportsMarkdown)
        {
            destination.Append("**");
        }

        destination.Append(' ');
    }

    private static string FormatCref(string cref) =>
        cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;

    private static string FormatCodeSpan(string value) => value.Contains(
        '`',
        StringComparison.Ordinal)
        ? string.Concat("``", value, "``")
        : string.Concat("`", value, "`");

    private static string CreateCodeFence(string value)
    {
        int longestRun = 0;
        int currentRun = 0;
        foreach (char character in value)
        {
            if (character == '`')
            {
                currentRun++;
                longestRun = Math.Max(longestRun, currentRun);
            }
            else
            {
                currentRun = 0;
            }
        }

        return new string('`', Math.Max(3, longestRun + 1));
    }

    private static string EscapeMarkdown(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (IsMarkdownCharacter(character))
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }

    private static string EscapeTableCell(string value) => value
        .Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("\r\n", "<br>", StringComparison.Ordinal)
        .Replace("\n", "<br>", StringComparison.Ordinal);

    private static bool TryNormalizeLink(string value, out string? normalizedLink)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
            uri.Scheme is "http" or "https")
        {
            normalizedLink = uri.AbsoluteUri
                .Replace("(", "%28", StringComparison.Ordinal)
                .Replace(")", "%29", StringComparison.Ordinal);
            return true;
        }

        normalizedLink = null;
        return false;
    }

    private static bool IsMarkdownCharacter(char value) => value is
        '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '(' or ')' or
        '#' or '+' or '-' or '.' or '!' or '<' or '>' or '|';
}
