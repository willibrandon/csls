namespace Csls.Workspaces;

/// <summary>
/// Describes one isolated design-time build-host request.
/// </summary>
internal sealed class MSBuildBuildHostRequest
{
    /// <summary>
    /// Initializes one build-host request.
    /// </summary>
    /// <param name="projectPaths">The absolute projects to load.</param>
    /// <param name="globalProperties">The MSBuild global properties.</param>
    public MSBuildBuildHostRequest(
        string[] projectPaths,
        Dictionary<string, string> globalProperties)
    {
        ArgumentNullException.ThrowIfNull(projectPaths);
        ArgumentNullException.ThrowIfNull(globalProperties);
        ProjectPaths = projectPaths;
        GlobalProperties = globalProperties;
    }

    /// <summary>
    /// Gets the absolute projects to load.
    /// </summary>
    public string[] ProjectPaths { get; }

    /// <summary>
    /// Gets the MSBuild global properties.
    /// </summary>
    public Dictionary<string, string> GlobalProperties { get; }
}
