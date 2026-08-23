#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Net;

const string Usage = "Usage: dotnet run --file scripts/Verify-Docs.cs";
const string SiteOrigin = "https://willibrandon.github.io";
const string SiteBasePath = "/csls/";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Verifies that generated documentation links and assets resolve within the site.")
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
foreach (string pagePath in Directory.EnumerateFiles(
    outputRoot,
    "*.html",
    SearchOption.AllDirectories))
{
    string relativePagePath = Path.GetRelativePath(outputRoot, pagePath)
        .Replace(Path.DirectorySeparatorChar, '/');
    var pageUri = new Uri(SiteOrigin + GetPagePath(relativePagePath));
    string html = await File.ReadAllTextAsync(pagePath).ConfigureAwait(false);
    foreach (string encodedTarget in EnumerateTargets(html))
    {
        string target = WebUtility.HtmlDecode(encodedTarget);
        if (!TryResolveLocalTarget(pageUri, target, out Uri targetUri))
        {
            continue;
        }

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
    $"Verified {checkedTargetCount} generated documentation links and assets.")
    .ConfigureAwait(false);
return 0;

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

static bool TryResolveLocalTarget(Uri pageUri, string target, out Uri targetUri)
{
    targetUri = null!;
    if (string.IsNullOrWhiteSpace(target) || target.StartsWith('#'))
    {
        if (!target.StartsWith('#'))
        {
            return false;
        }

        targetUri = new Uri(pageUri, target);
        return true;
    }

    if (!Uri.TryCreate(pageUri, target, out Uri? resolved) ||
        !string.Equals(resolved.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
        !string.Equals(resolved.Host, pageUri.Host, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    targetUri = resolved;
    return true;
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
