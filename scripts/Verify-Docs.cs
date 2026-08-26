#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Net;
using System.Text;

const string Usage = "Usage: dotnet run --file scripts/Verify-Docs.cs";
const string SiteOrigin = "https://willibrandon.github.io";
const string SiteBasePath = "/csls/";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Verifies generated documentation links, assets, and accessibility conditions.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(Usage).ConfigureAwait(false);
    return 0;
}

if (args.Length != 0)
{
    await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
    return 2;
}

string repositoryRoot = FindRepositoryRoot();
string outputRoot = Path.Join(repositoryRoot, "docs-site", "dist");
if (!Directory.Exists(outputRoot))
{
    await Console.Error.WriteLineAsync(
        "The generated documentation directory does not exist. Build the site first.")
        .ConfigureAwait(false);
    return 1;
}

var failures = new SortedSet<string>(StringComparer.Ordinal);
int checkedTargetCount = 0;
int checkedAccessibilityConditionCount = 0;
foreach (string pagePath in Directory.EnumerateFiles(
    outputRoot,
    "*.html",
    SearchOption.AllDirectories))
{
    string relativePagePath = Path.GetRelativePath(outputRoot, pagePath)
        .Replace(Path.DirectorySeparatorChar, '/');
    var pageUri = new Uri(SiteOrigin + GetPagePath(relativePagePath));
    string html = await File.ReadAllTextAsync(pagePath).ConfigureAwait(false);
    checkedAccessibilityConditionCount += VerifyAccessibility(
        relativePagePath,
        html,
        failures);
    foreach ((string target, Uri targetUri) in EnumerateTargets(html)
        .Select(static encodedTarget => WebUtility.HtmlDecode(encodedTarget))
        .Select(target => (Target: target, TargetUri: ResolveLocalTarget(pageUri, target)))
        .Where(static candidate => candidate.TargetUri is not null)
        .Select(static candidate => (
            candidate.Target,
            candidate.TargetUri ?? throw new InvalidDataException(
                "A filtered documentation target had no URI."))))
    {
        checkedTargetCount++;
        if (string.Equals(relativePagePath, "404.html", StringComparison.Ordinal) &&
            string.Equals(targetUri.AbsolutePath, SiteBasePath + "404/", StringComparison.Ordinal))
        {
            continue;
        }

        if (!targetUri.AbsolutePath.StartsWith(SiteBasePath, StringComparison.Ordinal))
        {
            failures.Add(
                $"{relativePagePath}: local target escapes {SiteBasePath}: {target}");
            continue;
        }

        string targetPath = Uri.UnescapeDataString(targetUri.AbsolutePath[SiteBasePath.Length..]);
        string? resolvedPath = ResolveOutputPath(outputRoot, targetPath);
        if (resolvedPath is null)
        {
            failures.Add($"{relativePagePath}: target does not exist: {target}");
            continue;
        }

        if (targetUri.Fragment.Length > 1 &&
            string.Equals(Path.GetExtension(resolvedPath), ".html", StringComparison.OrdinalIgnoreCase))
        {
            string identifier = Uri.UnescapeDataString(targetUri.Fragment[1..]);
            string targetHtml = await File.ReadAllTextAsync(resolvedPath).ConfigureAwait(false);
            if (!ContainsIdentifier(targetHtml, identifier))
            {
                failures.Add($"{relativePagePath}: fragment does not exist: {target}");
            }
        }
    }
}

if (failures.Count != 0)
{
    foreach (string failure in failures)
    {
        await Console.Error.WriteLineAsync(failure).ConfigureAwait(false);
    }

    return 1;
}

await Console.Out.WriteLineAsync(
    $"Verified {checkedTargetCount} generated documentation links and assets and " +
    $"{checkedAccessibilityConditionCount} accessibility conditions.")
    .ConfigureAwait(false);
return 0;

static int VerifyAccessibility(
    string relativePagePath,
    string html,
    ISet<string> failures)
{
    int conditionCount = 0;
    var identifiers = new HashSet<string>(StringComparer.Ordinal);
    foreach (string openingTag in EnumerateOpeningTags(html))
    {
        if (!TryGetAttribute(openingTag, "id", out string identifier) ||
            string.IsNullOrWhiteSpace(identifier))
        {
            continue;
        }

        conditionCount++;
        if (!identifiers.Add(identifier))
        {
            failures.Add($"{relativePagePath}: duplicate id: {identifier}");
        }
    }

    conditionCount++;
    string? htmlTag = EnumerateElements(html, "html", hasClosingTag: true)
        .Select(static element => element.OpeningTag)
        .FirstOrDefault();
    if (htmlTag is null ||
        !TryGetAttribute(htmlTag, "lang", out string language) ||
        string.IsNullOrWhiteSpace(language))
    {
        failures.Add($"{relativePagePath}: the document language is missing");
    }

    conditionCount++;
    string title = EnumerateElements(html, "title", hasClosingTag: true)
        .Select(static element => GetText(element.InnerHtml))
        .FirstOrDefault() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(title))
    {
        failures.Add($"{relativePagePath}: the document title is missing");
    }

    conditionCount++;
    (string OpeningTag, string InnerHtml)? main = null;
    foreach ((string openingTag, string innerHtml) in EnumerateElements(
        html,
        "main",
        hasClosingTag: true))
    {
        main = (openingTag, innerHtml);
        break;
    }
    if (main is null)
    {
        failures.Add($"{relativePagePath}: the main landmark is missing");
    }
    else
    {
        int headingLevel = 0;
        int firstLevelHeadingCount = 0;
        foreach ((int level, string openingTag, string innerHtml, _) in
            EnumerateHeadings(main.Value.InnerHtml))
        {
            if (IsHidden(openingTag))
            {
                continue;
            }

            conditionCount++;
            if (!HasAccessibleName(openingTag, innerHtml, identifiers))
            {
                failures.Add($"{relativePagePath}: h{level} has no accessible name");
            }

            if (level == 1)
            {
                firstLevelHeadingCount++;
            }

            conditionCount++;
            if (headingLevel != 0 && level > headingLevel + 1)
            {
                failures.Add(
                    $"{relativePagePath}: heading level skips from h{headingLevel} to h{level}");
            }

            headingLevel = level;
        }

        conditionCount++;
        if (firstLevelHeadingCount != 1)
        {
            failures.Add(
                $"{relativePagePath}: expected one h1 in main, found {firstLevelHeadingCount}");
        }
    }

    foreach ((string openingTag, _) in EnumerateElements(html, "img", hasClosingTag: false))
    {
        conditionCount++;
        if (!TryGetAttribute(openingTag, "alt", out _))
        {
            failures.Add($"{relativePagePath}: image is missing alt text");
        }
    }

    foreach (string tagName in new[] { "a", "button" })
    {
        foreach ((string openingTag, string innerHtml) in EnumerateElements(
            html,
            tagName,
            hasClosingTag: true))
        {
            if (IsHidden(openingTag))
            {
                continue;
            }

            conditionCount++;
            if (!HasAccessibleName(openingTag, innerHtml, identifiers))
            {
                failures.Add($"{relativePagePath}: {tagName} has no accessible name");
            }
        }
    }

    foreach (string tagName in new[] { "dialog", "nav" })
    {
        foreach ((string openingTag, string innerHtml) in EnumerateElements(
            html,
            tagName,
            hasClosingTag: true))
        {
            if (IsHidden(openingTag))
            {
                continue;
            }

            conditionCount++;
            if (!HasExplicitAccessibleName(openingTag, identifiers))
            {
                failures.Add($"{relativePagePath}: {tagName} has no accessible name");
            }
        }
    }

    foreach (string tagName in new[] { "input", "select", "textarea" })
    {
        bool hasClosingTag = !string.Equals(tagName, "input", StringComparison.Ordinal);
        foreach ((string openingTag, _) in EnumerateElements(html, tagName, hasClosingTag))
        {
            if (IsHidden(openingTag) ||
                (TryGetAttribute(openingTag, "type", out string inputType) &&
                string.Equals(inputType, "hidden", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            conditionCount++;
            if (!HasExplicitAccessibleName(openingTag, identifiers) &&
                !HasAssociatedLabel(openingTag, html))
            {
                failures.Add($"{relativePagePath}: {tagName} has no accessible name");
            }
        }
    }

    foreach (string openingTag in EnumerateOpeningTags(html))
    {
        foreach (string attributeName in new[]
            {
                "aria-controls",
                "aria-describedby",
                "aria-labelledby",
            })
        {
            if (!TryGetAttribute(openingTag, attributeName, out string references) ||
                string.IsNullOrWhiteSpace(references))
            {
                continue;
            }

            foreach (string reference in references.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                conditionCount++;
                if (!identifiers.Contains(reference))
                {
                    failures.Add(
                        $"{relativePagePath}: {attributeName} references missing id {reference}");
                }
            }
        }
    }

    return conditionCount;
}

static bool HasAccessibleName(
    string openingTag,
    string innerHtml,
    IReadOnlySet<string> identifiers)
{
    if (HasExplicitAccessibleName(openingTag, identifiers) ||
        !string.IsNullOrWhiteSpace(GetText(innerHtml)))
    {
        return true;
    }

    return EnumerateElements(innerHtml, "img", hasClosingTag: false)
        .Any(static element =>
            TryGetAttribute(element.OpeningTag, "alt", out string alternative) &&
            !string.IsNullOrWhiteSpace(alternative));
}

static bool HasExplicitAccessibleName(
    string openingTag,
    IReadOnlySet<string> identifiers)
{
    if ((TryGetAttribute(openingTag, "aria-label", out string label) &&
        !string.IsNullOrWhiteSpace(label)) ||
        (TryGetAttribute(openingTag, "title", out string title) &&
        !string.IsNullOrWhiteSpace(title)))
    {
        return true;
    }

    return TryGetAttribute(openingTag, "aria-labelledby", out string labelledBy) &&
        !string.IsNullOrWhiteSpace(labelledBy) &&
        labelledBy.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(identifiers.Contains);
}

static bool HasAssociatedLabel(string openingTag, string html)
{
    if (TryGetAttribute(openingTag, "id", out string identifier) &&
        !string.IsNullOrWhiteSpace(identifier) &&
        EnumerateElements(html, "label", hasClosingTag: true)
            .Any(element =>
                TryGetAttribute(element.OpeningTag, "for", out string target) &&
                string.Equals(target, identifier, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(GetText(element.InnerHtml))))
    {
        return true;
    }

    int elementStart = html.IndexOf(openingTag, StringComparison.Ordinal);
    if (elementStart < 0)
    {
        return false;
    }

    int labelStart = html.LastIndexOf(
        "<label",
        elementStart,
        StringComparison.OrdinalIgnoreCase);
    if (labelStart < 0)
    {
        return false;
    }

    int labelOpeningEnd = FindTagEnd(html, labelStart + "<label".Length);
    int labelClosingStart = html.IndexOf(
        "</label",
        elementStart,
        StringComparison.OrdinalIgnoreCase);
    if (labelOpeningEnd < 0 ||
        labelOpeningEnd >= elementStart ||
        labelClosingStart < elementStart)
    {
        return false;
    }

    string labelContent = html[(labelOpeningEnd + 1)..labelClosingStart];
    return !string.IsNullOrWhiteSpace(GetText(labelContent));
}

static bool IsHidden(string openingTag) =>
    (TryGetAttribute(openingTag, "aria-hidden", out string ariaHidden) &&
    string.Equals(ariaHidden, "true", StringComparison.OrdinalIgnoreCase)) ||
    TryGetAttribute(openingTag, "hidden", out _);

static IEnumerable<(int Level, string OpeningTag, string InnerHtml, int Position)>
    EnumerateHeadings(
    string html)
{
    var headings = new List<(int Level, string OpeningTag, string InnerHtml, int Position)>();
    for (int level = 1; level <= 6; level++)
    {
        int searchStart = 0;
        foreach ((string openingTag, string innerHtml) in EnumerateElements(
            html,
            $"h{level}",
            hasClosingTag: true))
        {
            int position = html.IndexOf(openingTag, searchStart, StringComparison.Ordinal);
            headings.Add((level, openingTag, innerHtml, position));
            searchStart = position + openingTag.Length;
        }
    }

    foreach ((int level, string openingTag, string innerHtml, int position) in
        headings.OrderBy(static heading => heading.Position))
    {
        yield return (level, openingTag, innerHtml, position);
    }
}

static string FindRepositoryRoot()
{
    DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("The csls repository root was not found.");
}

static string GetPagePath(string relativePagePath)
{
    const string IndexFileName = "index.html";
    if (string.Equals(relativePagePath, IndexFileName, StringComparison.Ordinal))
    {
        return SiteBasePath;
    }

    if (relativePagePath.EndsWith(IndexFileName, StringComparison.Ordinal))
    {
        return SiteBasePath + relativePagePath[..^IndexFileName.Length];
    }

    return SiteBasePath + relativePagePath;
}

static IEnumerable<string> EnumerateTargets(string html)
{
    string[] prefixes = ["href=\"", "src=\""];
    foreach (string prefix in prefixes)
    {
        int searchStart = 0;
        while (searchStart < html.Length)
        {
            int valueStart = html.IndexOf(
                prefix,
                searchStart,
                StringComparison.OrdinalIgnoreCase);
            if (valueStart < 0)
            {
                break;
            }

            valueStart += prefix.Length;
            int valueEnd = html.IndexOf('"', valueStart);
            if (valueEnd < 0)
            {
                break;
            }

            yield return html[valueStart..valueEnd];
            searchStart = valueEnd + 1;
        }
    }
}

static Uri? ResolveLocalTarget(Uri pageUri, string target)
{
    if (string.IsNullOrWhiteSpace(target) || target.StartsWith('#'))
    {
        return target.StartsWith('#') ? new Uri(pageUri, target) : null;
    }

    if (!Uri.TryCreate(pageUri, target, out Uri? resolved) ||
        !string.Equals(resolved.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
        !string.Equals(resolved.Host, pageUri.Host, StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    return resolved;
}

static string? ResolveOutputPath(string outputRoot, string targetPath)
{
    string platformPath = targetPath.Replace('/', Path.DirectorySeparatorChar);
    string candidate = Path.GetFullPath(Path.Join(outputRoot, platformPath));
    if (!candidate.StartsWith(
            Path.GetFullPath(outputRoot) + Path.DirectorySeparatorChar,
            StringComparison.Ordinal) &&
        !string.Equals(candidate, Path.GetFullPath(outputRoot), StringComparison.Ordinal))
    {
        return null;
    }

    if (File.Exists(candidate))
    {
        return candidate;
    }

    string indexCandidate = Path.Join(candidate, "index.html");
    return File.Exists(indexCandidate) ? indexCandidate : null;
}

static bool ContainsIdentifier(string html, string identifier)
{
    string encodedIdentifier = WebUtility.HtmlEncode(identifier);
    return html.Contains($"id=\"{encodedIdentifier}\"", StringComparison.OrdinalIgnoreCase) ||
        html.Contains($"name=\"{encodedIdentifier}\"", StringComparison.OrdinalIgnoreCase);
}

static IEnumerable<string> EnumerateOpeningTags(string html)
{
    int searchStart = 0;
    while (searchStart < html.Length)
    {
        int openingStart = html.IndexOf('<', searchStart);
        if (openingStart < 0 || openingStart + 1 >= html.Length)
        {
            yield break;
        }

        char firstCharacter = html[openingStart + 1];
        if (firstCharacter is '/' or '!' or '?' || !char.IsAsciiLetter(firstCharacter))
        {
            searchStart = openingStart + 1;
            continue;
        }

        int openingEnd = FindTagEnd(html, openingStart + 1);
        if (openingEnd < 0)
        {
            yield break;
        }

        yield return html[openingStart..(openingEnd + 1)];
        searchStart = openingEnd + 1;
    }
}

static IEnumerable<(string OpeningTag, string InnerHtml)> EnumerateElements(
    string html,
    string tagName,
    bool hasClosingTag)
{
    string openingPrefix = $"<{tagName}";
    string closingPrefix = $"</{tagName}";
    int searchStart = 0;
    while (searchStart < html.Length)
    {
        int openingStart = html.IndexOf(
            openingPrefix,
            searchStart,
            StringComparison.OrdinalIgnoreCase);
        if (openingStart < 0)
        {
            yield break;
        }

        int nameEnd = openingStart + openingPrefix.Length;
        if (nameEnd < html.Length &&
            !char.IsWhiteSpace(html[nameEnd]) &&
            html[nameEnd] is not '>' and not '/')
        {
            searchStart = nameEnd;
            continue;
        }

        int openingEnd = FindTagEnd(html, nameEnd);
        if (openingEnd < 0)
        {
            yield break;
        }

        string openingTag = html[openingStart..(openingEnd + 1)];
        if (!hasClosingTag)
        {
            yield return (openingTag, string.Empty);
            searchStart = openingEnd + 1;
            continue;
        }

        int closingStart = html.IndexOf(
            closingPrefix,
            openingEnd + 1,
            StringComparison.OrdinalIgnoreCase);
        if (closingStart < 0)
        {
            yield break;
        }

        int closingEnd = FindTagEnd(html, closingStart + closingPrefix.Length);
        if (closingEnd < 0)
        {
            yield break;
        }

        yield return (
            openingTag,
            html[(openingEnd + 1)..closingStart]);
        searchStart = closingEnd + 1;
    }
}

static int FindTagEnd(string html, int searchStart)
{
    char quote = '\0';
    for (int index = searchStart; index < html.Length; index++)
    {
        char character = html[index];
        if (quote == '\0' && (character == '\'' || character == '"'))
        {
            quote = character;
        }
        else if (quote != '\0' && character == quote)
        {
            quote = '\0';
        }
        else if (quote == '\0' && character == '>')
        {
            return index;
        }
    }

    return -1;
}

static bool TryGetAttribute(
    string openingTag,
    string attributeName,
    out string value)
{
    int index = 1;
    while (index < openingTag.Length &&
        !char.IsWhiteSpace(openingTag[index]) &&
        openingTag[index] is not '>' and not '/')
    {
        index++;
    }

    while (index < openingTag.Length)
    {
        while (index < openingTag.Length && char.IsWhiteSpace(openingTag[index]))
        {
            index++;
        }

        if (index >= openingTag.Length || openingTag[index] is '>' or '/')
        {
            break;
        }

        int nameStart = index;
        while (index < openingTag.Length &&
            !char.IsWhiteSpace(openingTag[index]) &&
            openingTag[index] is not '=' and not '>' and not '/')
        {
            index++;
        }

        string name = openingTag[nameStart..index];
        while (index < openingTag.Length && char.IsWhiteSpace(openingTag[index]))
        {
            index++;
        }

        string attributeValue = string.Empty;
        if (index < openingTag.Length && openingTag[index] == '=')
        {
            index++;
            while (index < openingTag.Length && char.IsWhiteSpace(openingTag[index]))
            {
                index++;
            }

            if (index < openingTag.Length &&
                (openingTag[index] == '\'' || openingTag[index] == '"'))
            {
                char quote = openingTag[index++];
                int valueStart = index;
                while (index < openingTag.Length && openingTag[index] != quote)
                {
                    index++;
                }

                attributeValue = openingTag[valueStart..index];
                if (index < openingTag.Length)
                {
                    index++;
                }
            }
            else
            {
                int valueStart = index;
                while (index < openingTag.Length &&
                    !char.IsWhiteSpace(openingTag[index]) &&
                    openingTag[index] is not '>')
                {
                    index++;
                }

                attributeValue = openingTag[valueStart..index];
            }
        }

        if (string.Equals(name, attributeName, StringComparison.OrdinalIgnoreCase))
        {
            value = WebUtility.HtmlDecode(attributeValue);
            return true;
        }
    }

    value = string.Empty;
    return false;
}

static string GetText(string html)
{
    var text = new StringBuilder(html.Length);
    bool insideTag = false;
    foreach (char character in html)
    {
        if (character == '<')
        {
            insideTag = true;
            continue;
        }

        if (character == '>')
        {
            insideTag = false;
            text.Append(' ');
            continue;
        }

        if (!insideTag)
        {
            text.Append(character);
        }
    }

    return WebUtility.HtmlDecode(text.ToString()).Trim();
}
