using Microsoft.CodeAnalysis.MSBuild;

namespace Csls.Workspaces;

/// <summary>
/// Observes the final resolution operation for projects in one MSBuild workspace load.
/// </summary>
internal sealed class WorkspaceProjectLoadObserver : IProgress<ProjectLoadProgress>
{
    private readonly WorkspaceLoadProgressReporter _reporter;
    private readonly string _loadIdentity;

    /// <summary>
    /// Initializes an observer for one independently loaded solution or project.
    /// </summary>
    /// <param name="reporter">The shared workspace progress reporter.</param>
    /// <param name="loadIdentity">The stable identity of the owning workspace load.</param>
    internal WorkspaceProjectLoadObserver(
        WorkspaceLoadProgressReporter reporter,
        string loadIdentity)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        ArgumentException.ThrowIfNullOrWhiteSpace(loadIdentity);
        _reporter = reporter;
        _loadIdentity = loadIdentity;
    }

    /// <summary>
    /// Records the first final resolution for each project path.
    /// </summary>
    /// <param name="value">The completed MSBuild load operation.</param>
    public void Report(ProjectLoadProgress value)
    {
        _reporter.ObserveProject(_loadIdentity, value.FilePath);
        if (value.Operation == ProjectLoadOperation.Resolve)
        {
            _reporter.ReportProject(_loadIdentity, value.FilePath);
        }
    }
}
