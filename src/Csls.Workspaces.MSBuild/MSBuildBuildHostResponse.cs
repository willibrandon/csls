namespace Csls.Workspaces;

/// <summary>
/// Carries completed design-time project states and diagnostics to the loader.
/// </summary>
internal sealed class MSBuildBuildHostResponse
{
    /// <summary>
    /// Initializes one completed build-host response.
    /// </summary>
    /// <param name="snapshots">The completed project states.</param>
    /// <param name="diagnostics">The reported build diagnostics.</param>
    public MSBuildBuildHostResponse(
        MSBuildProjectSnapshot[] snapshots,
        MSBuildBuildHostDiagnostic[] diagnostics)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(diagnostics);
        Snapshots = snapshots;
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// Gets the completed project states.
    /// </summary>
    public MSBuildProjectSnapshot[] Snapshots { get; }

    /// <summary>
    /// Gets the reported build diagnostics.
    /// </summary>
    public MSBuildBuildHostDiagnostic[] Diagnostics { get; }
}
