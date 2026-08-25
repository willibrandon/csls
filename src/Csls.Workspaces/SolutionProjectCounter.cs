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
        return Path.GetExtension(solutionPath).Equals(
            ".slnx",
            StringComparison.OrdinalIgnoreCase)
            ? CountXmlSolutionProjects(solutionPath)
            : CountClassicSolutionProjects(solutionPath);
    }

    private static int CountXmlSolutionProjects(string solutionPath)
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
            .Count(static element =>
                element.Name.LocalName.Equals("Project", StringComparison.Ordinal) &&
                element.Attribute("Path")?.Value.EndsWith(
                    ".csproj",
                    StringComparison.OrdinalIgnoreCase) == true);
    }

    private static int CountClassicSolutionProjects(string solutionPath)
        => File.ReadLines(solutionPath).Count(static line => IsClassicCSharpProject(line));

    private static bool IsClassicCSharpProject(ReadOnlySpan<char> line)
    {
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
        if (!TryReadQuotedField(remainder, ref position, out _, out _) ||
            !TryConsumeComma(remainder, ref position) ||
            !TryReadQuotedField(
                remainder,
                ref position,
                out int projectPathStart,
                out int projectPathLength) ||
            !TryConsumeComma(remainder, ref position))
        {
            return false;
        }

        return remainder
            .Slice(projectPathStart, projectPathLength)
            .EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadQuotedField(
        ReadOnlySpan<char> input,
        ref int position,
        out int valueStart,
        out int valueLength)
    {
        SkipWhitespace(input, ref position);
        valueStart = 0;
        valueLength = 0;
        if ((uint)position >= (uint)input.Length || input[position] != '"')
        {
            return false;
        }

        valueStart = ++position;
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

            valueLength = position - valueStart;
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
}
