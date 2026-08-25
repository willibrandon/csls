namespace Csls.Workspaces;

/// <summary>
/// Resolves installed Mono reference assemblies for legacy .NET Framework projects.
/// </summary>
internal static class LegacyFrameworkReferenceResolver
{
    /// <summary>
    /// Adds the installed platform framework root when Mono provides one.
    /// </summary>
    /// <param name="globalProperties">The mutable MSBuild global properties.</param>
    internal static void AddGlobalProperties(IDictionary<string, string> globalProperties)
    {
        ArgumentNullException.ThrowIfNull(globalProperties);
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string[] candidates =
        [
            "/usr/lib/mono/xbuild-frameworks",
            "/usr/local/lib/mono/xbuild-frameworks",
            "/opt/homebrew/lib/mono/xbuild-frameworks",
            "/Library/Frameworks/Mono.framework/Versions/Current/lib/mono/xbuild-frameworks",
            "/Library/Frameworks/Mono.framework/External/xbuild-frameworks"
        ];
        string? frameworkRoot = candidates.FirstOrDefault(Directory.Exists);
        if (frameworkRoot is null)
        {
            return;
        }

        globalProperties["TargetFrameworkRootPath"] =
            Path.TrimEndingDirectorySeparator(frameworkRoot) + Path.DirectorySeparatorChar;
        string? monoRoot = Path.GetDirectoryName(frameworkRoot);
        string? extensionTargetPath = monoRoot is null
            ? null
            : Path.Join(
                monoRoot,
                "xbuild",
                "Microsoft",
                "Microsoft.NET.Build.Extensions",
                "Microsoft.NET.Build.Extensions.targets");
        if (extensionTargetPath is not null && File.Exists(extensionTargetPath))
        {
            globalProperties["MicrosoftNETBuildExtensionsTargets"] = extensionTargetPath;
        }
    }
}
