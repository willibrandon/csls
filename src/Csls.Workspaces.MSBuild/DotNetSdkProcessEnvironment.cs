using System.Diagnostics;

namespace Csls.Workspaces;

/// <summary>
/// Keeps SDK-bound MSBuild state out of child processes that select their own SDK.
/// </summary>
internal static class DotNetSdkProcessEnvironment
{
    /// <summary>
    /// Allows a child process to select its SDK from its working directory and global.json.
    /// </summary>
    /// <param name="startInfo">The process launch configuration to isolate.</param>
    internal static void UseWorkspaceSdk(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.Environment.Remove("MSBUILD_EXE_PATH");
        startInfo.Environment.Remove("MSBuildExtensionsPath");
        startInfo.Environment.Remove("MSBuildSDKsPath");
        foreach (string key in startInfo.Environment.Keys
            .Where(static key => key.StartsWith(
                "DOTNET_ROOT",
                StringComparison.OrdinalIgnoreCase))
            .ToArray())
        {
            startInfo.Environment.Remove(key);
        }

        if (Path.IsPathFullyQualified(startInfo.FileName))
        {
            startInfo.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(
                startInfo.FileName) ?? throw new InvalidOperationException(
                    $"The .NET host path has no parent directory: {startInfo.FileName}");
        }
    }
}
