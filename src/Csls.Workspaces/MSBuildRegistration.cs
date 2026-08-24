using Microsoft.Build.Locator;

namespace Csls.Workspaces;

/// <summary>
/// Registers the workspace-selected .NET SDK for in-process MSBuild assembly resolution.
/// </summary>
internal static class MSBuildRegistration
{
    private static readonly Lock s_registrationLock = new();

    /// <summary>
    /// Registers the first compatible MSBuild instance resolved from the workspace directory.
    /// </summary>
    /// <param name="workspaceFile">The absolute solution, project, or file-app path.</param>
    /// <returns>The newly registered instance, or null when registration already exists.</returns>
    internal static VisualStudioInstance? EnsureRegistered(string workspaceFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceFile);
        lock (s_registrationLock)
        {
            if (MSBuildLocator.IsRegistered)
            {
                return null;
            }

            string workingDirectory = Directory.Exists(workspaceFile)
                ? workspaceFile
                : Path.GetDirectoryName(workspaceFile)
                    ?? throw new ArgumentException(
                        "The workspace file has no parent directory.",
                        nameof(workspaceFile));
            VisualStudioInstanceQueryOptions options = VisualStudioInstanceQueryOptions.Default;
            options.WorkingDirectory = workingDirectory;
            VisualStudioInstance instance = MSBuildLocator.QueryVisualStudioInstances(options)
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"No compatible .NET SDK was found for {workingDirectory}.");
            MSBuildLocator.RegisterInstance(instance);
            return instance;
        }
    }
}
