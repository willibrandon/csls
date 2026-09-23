using System.Xml;
using System.Xml.Linq;

namespace Csls.Workspaces;

/// <summary>
/// Counts C# project entries without loading MSBuild before its registered toolset is active.
/// </summary>
internal static class SolutionProjectCounter
{
    /// <summary>
    /// Counts C# project entries in one XML or classic solution file.
    /// </summary>
    /// <param name="solutionPath">The absolute solution path.</param>
    /// <returns>The number of C# project entries.</returns>
    internal static int CountCSharpProjects(string solutionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);
        return ReadCSharpProjectPaths(solutionPath).Count;
    }

    /// <summary>
    /// Reads absolute C# project paths from an XML or classic solution without evaluating MSBuild.
    /// </summary>
    /// <param name="solutionPath">The absolute solution path.</param>
    /// <returns>The distinct project paths in solution order.</returns>
    internal static IReadOnlyList<string> ReadCSharpProjectPaths(string solutionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);
        string fullSolutionPath = Path.GetFullPath(solutionPath);
        IEnumerable<string> relativePaths = Path.GetExtension(fullSolutionPath).Equals(
            ".slnx",
            StringComparison.OrdinalIgnoreCase)
            ? ReadXmlSolutionProjectPaths(fullSolutionPath)
            : ReadClassicSolutionProjectPaths(fullSolutionPath);
        string solutionDirectory = Path.GetDirectoryName(fullSolutionPath)
            ?? throw new InvalidDataException(
                $"Solution path has no parent directory: {fullSolutionPath}");
        return
        [
            .. relativePaths
                .Select(path => Path.GetFullPath(
                    NormalizeSolutionPath(path),
                    solutionDirectory))
                .Distinct(PathComparer)
        ];
    }

    private static IEnumerable<string> ReadXmlSolutionProjectPaths(string solutionPath)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
        using var reader = XmlReader.Create(solutionPath, settings);
        var solution = XDocument.Load(reader, LoadOptions.None);
        return solution
            .Descendants()
            .Where(static element =>
                element.Name.LocalName.Equals("Project", StringComparison.Ordinal))
            .Select(static element => element.Attribute("Path"))
            .Where(static attribute =>
                attribute is not null &&
                attribute.Value.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(static attribute => attribute!.Value);
    }

    private static IEnumerable<string> ReadClassicSolutionProjectPaths(string solutionPath) =>
        File.ReadLines(solutionPath)
            .Select(static line =>
                TryReadClassicCSharpProjectPath(line, out string? projectPath)
                    ? projectPath
                    : null)
            .Where(static projectPath => projectPath is not null)
            .Select(static projectPath => projectPath!);

    private static bool TryReadClassicCSharpProjectPath(
        ReadOnlySpan<char> line,
        out string? projectPath)
    {
        projectPath = null;
        ReadOnlySpan<char> remainder = line.TrimStart();
        if (!remainder.StartsWith("Project(", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int assignmentIndex = remainder.IndexOf('=');
        if (assignmentIndex < 0)
        {
            return false;
        }

        int position = assignmentIndex + 1;
        if (!TryReadQuotedField(remainder, ref position, out _) ||
            !TryConsumeComma(remainder, ref position) ||
            !TryReadQuotedField(
                remainder,
                ref position,
                out Range projectPathRange) ||
            !TryConsumeComma(remainder, ref position))
        {
            return false;
        }

        ReadOnlySpan<char> projectPathSpan = remainder[projectPathRange];
        if (!projectPathSpan.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        projectPath = projectPathSpan.ToString().Replace("\"\"", "\"", StringComparison.Ordinal);
        return true;
    }

    private static bool TryReadQuotedField(
        ReadOnlySpan<char> input,
        ref int position,
        out Range valueRange)
    {
        SkipWhitespace(input, ref position);
        valueRange = default;
        if ((uint)position >= (uint)input.Length || input[position] != '"')
        {
            return false;
        }

        int valueStart = ++position;
        while (position < input.Length)
        {
            if (input[position] != '"')
            {
                position++;
                continue;
            }

            if (position + 1 < input.Length && input[position + 1] == '"')
            {
                position += 2;
                continue;
            }

            valueRange = valueStart..position;
            position++;
            return true;
        }

        return false;
    }

    private static bool TryConsumeComma(ReadOnlySpan<char> input, ref int position)
    {
        SkipWhitespace(input, ref position);
        if ((uint)position >= (uint)input.Length || input[position] != ',')
        {
            return false;
        }

        position++;
        return true;
    }

    private static void SkipWhitespace(ReadOnlySpan<char> input, ref int position)
    {
        while (position < input.Length && char.IsWhiteSpace(input[position]))
        {
            position++;
        }
    }

    private static string NormalizeSolutionPath(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
