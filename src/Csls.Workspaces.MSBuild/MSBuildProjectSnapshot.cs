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
    /// <param name="project">The bounded project state returned by the build host.</param>
    /// <param name="inputPaths">The evaluated files and directories that define the state.</param>
    /// <param name="projectReferencePaths">The evaluated absolute project-reference paths.</param>
    public MSBuildProjectSnapshot(
        string projectPath,
        MSBuildProjectData project,
        string[] inputPaths,
        string[] projectReferencePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentNullException.ThrowIfNull(projectReferencePaths);
        ProjectPath = projectPath;
        Project = project;
        InputPaths = inputPaths;
        ProjectReferencePaths =
        [
            .. projectReferencePaths.Distinct(PathComparer)
        ];
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
    public string ProjectPath { get; }

    /// <summary>
    /// Gets the bounded project state returned by the completed design-time build.
    /// </summary>
    public MSBuildProjectData Project { get; }

    /// <summary>
    /// Gets the evaluated files and directories that define the project state.
    /// </summary>
    public string[] InputPaths { get; }

    /// <summary>
    /// Gets the absolute project-reference paths discovered during evaluation.
    /// </summary>
    public string[] ProjectReferencePaths { get; }

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
