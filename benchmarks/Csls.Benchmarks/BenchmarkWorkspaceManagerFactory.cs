using Csls.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Csls.Benchmarks;

/// <summary>
/// Creates production desktop workspace managers for benchmarks.
/// </summary>
internal static class BenchmarkWorkspaceManagerFactory
{
    /// <summary>
    /// Creates a workspace manager with the production desktop loader.
    /// </summary>
    /// <returns>A new production desktop workspace manager.</returns>
    internal static WorkspaceManager Create() =>
        new(
            NullLogger<WorkspaceManager>.Instance,
            new MSBuildWorkspaceLoader(NullLogger<MSBuildWorkspaceLoader>.Instance));
}
