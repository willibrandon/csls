using Microsoft.Build.Execution;

namespace Csls.Workspaces;

/// <summary>
/// Carries one completed design-time project state into Roslyn workspace construction.
/// </summary>
internal sealed class MSBuildProjectSnapshot
{
    /// <summary>
    /// Initializes one completed design-time project state.
    /// </summary>
    /// <param name="projectPath">The absolute project file path.</param>
    /// <param name="projectInstance">The project state returned by MSBuild.</param>
    /// <param name="inputPaths">The evaluated files and directories that define the state.</param>
    /// <param name="projectReferencePaths">The evaluated absolute project-reference paths.</param>
    internal MSBuildProjectSnapshot(
        string projectPath,
        ProjectInstance projectInstance,
        IEnumerable<string> inputPaths,
        IEnumerable<string> projectReferencePaths)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentNullException.ThrowIfNull(projectReferencePaths);
        ProjectPath = projectPath;
        ProjectInstance = projectInstance;
        ProjectReferencePaths = projectReferencePaths
            .Distinct(PathComparer)
            .ToArray();
        _inputStamps = inputPaths
            .Distinct(PathComparer)
            .ToDictionary(
                static path => path,
                GetInputStamp,
                PathComparer);
    }

    private readonly IReadOnlyDictionary<string, long> _inputStamps;

    /// <summary>
    /// Gets the absolute project file path.
    /// </summary>
    internal string ProjectPath { get; }

    /// <summary>
    /// Gets the project state returned by the completed design-time build.
    /// </summary>
    internal ProjectInstance ProjectInstance { get; }

    /// <summary>
    /// Gets the absolute project-reference paths discovered during evaluation.
    /// </summary>
    internal IReadOnlyList<string> ProjectReferencePaths { get; }

    /// <summary>
    /// Determines whether every evaluated project input still matches this state.
    /// </summary>
    /// <returns>True when the design-time state can be reused.</returns>
    internal bool IsCurrent() =>
        _inputStamps.All(static input => GetInputStamp(input.Key) == input.Value);

    private static long GetInputStamp(string path)
    {
        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            return unchecked((file.LastWriteTimeUtc.Ticks * 397) ^ file.Length);
        }

        return Directory.Exists(path)
            ? Directory.GetLastWriteTimeUtc(path).Ticks ^ long.MaxValue
            : long.MinValue;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
