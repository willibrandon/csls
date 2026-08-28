using Microsoft.CodeAnalysis;
using System.Collections.Immutable;

namespace Csls.Workspaces;

/// <summary>
/// Selects the Roslyn project context used for a source document path.
/// </summary>
internal static class WorkspaceDocumentSelector
{
    /// <summary>
    /// Selects a deterministic source document, preferring the best target-framework flavor.
    /// </summary>
    /// <param name="solution">The current Roslyn solution.</param>
    /// <param name="path">The absolute source document path.</param>
    /// <returns>The selected source document, or <see langword="null"/> when none exists.</returns>
    internal static Document? SelectSourceDocument(Solution solution, string path)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ImmutableArray<DocumentId> documentIds = solution.GetDocumentIdsWithFilePath(path);
        Document[] documents =
        [
            .. documentIds
                .Select(solution.GetDocument)
                .OfType<Document>()
        ];
        return documents.Length switch
        {
            0 => null,
            1 => documents[0],
            _ => SelectProjectContext(documents)
        };
    }

    private static Document SelectProjectContext(Document[] documents)
    {
        string? projectPath = documents[0].Project.FilePath;
        bool containsOneMultiTargetedProject = !string.IsNullOrWhiteSpace(projectPath) &&
            documents.All(document => string.Equals(
                document.Project.FilePath,
                projectPath,
                PathComparison));
        if (!containsOneMultiTargetedProject)
        {
            return documents
                .OrderBy(static document => document.Project.FilePath, PathComparer)
                .ThenBy(static document => document.Project.Name, StringComparer.Ordinal)
                .First();
        }

        (Document Document, (int Family, bool HasPlatform, Version Version) Preference)[]
            frameworks =
        [
            .. documents
                .Select(static document =>
                    (Document: document, Preference: ParseFramework(document)))
                .Where(static item => item.Preference is not null)
                .Select(static item => (item.Document, item.Preference!.Value))
        ];
        if (frameworks.Length == 0)
        {
            return documents.OrderBy(
                static document => document.Project.Name,
                StringComparer.Ordinal).First();
        }

        return frameworks
            .OrderByDescending(static item => item.Preference.Family)
            .ThenByDescending(static item => item.Preference.HasPlatform)
            .ThenByDescending(static item => item.Preference.Version)
            .ThenBy(
                static item => item.Document.Project.Name,
                StringComparer.Ordinal)
            .Select(static item => item.Document)
            .First();
    }

    private static (int Family, bool HasPlatform, Version Version)? ParseFramework(
        Document document)
    {
        string name = document.Project.Name;
        int openParenthesis = name.LastIndexOf('(');
        if (openParenthesis < 0 || name[^1] != ')')
        {
            return null;
        }

        string targetFramework = name[(openParenthesis + 1)..^1];
        int platformSeparator = targetFramework.IndexOf('-', StringComparison.Ordinal);
        bool hasPlatform = platformSeparator >= 0;
        string framework = hasPlatform
            ? targetFramework[..platformSeparator]
            : targetFramework;
        if (framework.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) &&
            Version.TryParse(framework[11..], out Version? standardVersion))
        {
            return (2, hasPlatform, standardVersion);
        }

        if (framework.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase) &&
            Version.TryParse(framework[10..], out Version? coreVersion))
        {
            return (3, hasPlatform, coreVersion);
        }

        if (!framework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string versionText = framework[3..];
        if (versionText.Contains('.', StringComparison.Ordinal) &&
            Version.TryParse(versionText, out Version? netVersion))
        {
            return (4, hasPlatform, netVersion);
        }

        if (versionText.Length is < 2 or > 3 ||
            !versionText.All(char.IsAsciiDigit))
        {
            return null;
        }

        int major = versionText[0] - '0';
        int minor = versionText[1] - '0';
        int patch = versionText.Length == 3 ? versionText[2] - '0' : 0;
        return (1, hasPlatform, new Version(major, minor, patch));
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
