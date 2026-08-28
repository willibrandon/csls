using Csls.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Csls.Tests;

/// <summary>
/// Creates production desktop workspace managers for integration tests.
/// </summary>
internal static class WorkspaceManagerTestFactory
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
