namespace Csls.Workspaces;

/// <summary>
/// Converts concurrent MSBuild operations into ordered per-project workspace progress.
/// </summary>
internal sealed class WorkspaceLoadProgressReporter
{
    private readonly HashSet<string> _completedProjects = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly HashSet<string> _observedProjects = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly IProgress<WorkspaceLoadProgress> _progress;
    private readonly int _expectedProjectCount;
    private int _lastPercentage;

    /// <summary>
    /// Initializes a reporter with the projects known before workspace evaluation starts.
    /// </summary>
    /// <param name="expectedProjectCount">The initial number of solution and project entries.</param>
    /// <param name="progress">The synchronous destination for ordered project completions.</param>
    internal WorkspaceLoadProgressReporter(
        int expectedProjectCount,
        IProgress<WorkspaceLoadProgress> progress)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedProjectCount);
        ArgumentNullException.ThrowIfNull(progress);
        _expectedProjectCount = expectedProjectCount;
        _progress = progress;
    }

    /// <summary>
    /// Widens the current project total when Roslyn discovers a new referenced project.
    /// </summary>
    /// <param name="loadIdentity">The identity of the owning workspace load.</param>
    /// <param name="projectPath">The absolute project path.</param>
    internal void ObserveProject(string loadIdentity, string projectPath)
    {
        (string _, string projectIdentity) = GetProjectIdentity(loadIdentity, projectPath);
        lock (_gate)
        {
            _observedProjects.Add(projectIdentity);
        }
    }

    /// <summary>
    /// Reports one project after its final Roslyn resolution or fallback solution enumeration.
    /// </summary>
    /// <param name="loadIdentity">The identity of the owning workspace load.</param>
    /// <param name="projectPath">The absolute project path.</param>
    /// <param name="projectName">The optional display name for a project without a project file.</param>
    internal void ReportProject(
        string loadIdentity,
        string projectPath,
        string? projectName = null)
    {
        (string fullPath, string projectIdentity) = GetProjectIdentity(
            loadIdentity,
            projectPath);
        lock (_gate)
        {
            _observedProjects.Add(projectIdentity);
            if (!_completedProjects.Add(projectIdentity))
            {
                return;
            }

            int completedProjects = _completedProjects.Count;
            int totalProjects = Math.Max(
                _expectedProjectCount,
                _observedProjects.Count);
            int percentage = Math.Max(
                _lastPercentage,
                checked(completedProjects * 100 / Math.Max(1, totalProjects)));
            _lastPercentage = percentage;
            _progress.Report(new WorkspaceLoadProgress
            {
                ProjectName = projectName ?? Path.GetFileNameWithoutExtension(fullPath),
                CompletedProjects = completedProjects,
                TotalProjects = totalProjects,
                Percentage = percentage
            });
        }
    }

    private static (string FullPath, string ProjectIdentity) GetProjectIdentity(
        string loadIdentity,
        string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loadIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        string fullPath = Path.GetFullPath(projectPath);
        return (fullPath, string.Concat(loadIdentity, "\0", fullPath));
    }
}
