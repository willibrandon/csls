using System.Xml;
using System.Xml.Linq;

namespace Csls.Workspaces;

/// <summary>
/// Resolves installed Mono reference assemblies for legacy .NET Framework projects.
/// </summary>
internal static class LegacyFrameworkReferenceResolver
{
    /// <summary>
    /// Adds an installed Mono framework only when project-provided references are unavailable.
    /// </summary>
    /// <param name="globalProperties">The mutable MSBuild global properties.</param>
    /// <param name="frameworkIdentifier">The evaluated target framework identifier.</param>
    /// <param name="frameworkVersion">The evaluated target framework version.</param>
    /// <returns>True when a complete installed framework was selected.</returns>
    internal static bool AddFallbackGlobalProperties(
        IDictionary<string, string> globalProperties,
        string frameworkIdentifier,
        string frameworkVersion)
    {
        ArgumentNullException.ThrowIfNull(globalProperties);
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkVersion);
        if (OperatingSystem.IsWindows() || !string.Equals(
            frameworkIdentifier,
            ".NETFramework",
            StringComparison.OrdinalIgnoreCase))
        {
            return false;
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
            return false;
        }

        string frameworkDirectory = Path.Join(
            frameworkRoot,
            frameworkIdentifier,
            frameworkVersion);
        string? referenceDirectory = ResolveReferenceDirectory(frameworkDirectory);
        if (referenceDirectory is null)
        {
            return false;
        }

        globalProperties["TargetFrameworkRootPath"] =
            Path.TrimEndingDirectorySeparator(frameworkRoot) + Path.DirectorySeparatorChar;
        globalProperties["TargetFrameworkDirectory"] = referenceDirectory;
        globalProperties["TargetFrameworkDirectories"] = referenceDirectory;
        globalProperties["FrameworkPathOverride"] = referenceDirectory;
        return true;
    }

    private static string? ResolveReferenceDirectory(string frameworkDirectory)
    {
        if (File.Exists(Path.Join(frameworkDirectory, "mscorlib.dll")))
        {
            return frameworkDirectory;
        }

        string frameworkListPath = Path.Join(
            frameworkDirectory,
            "RedistList",
            "FrameworkList.xml");
        if (!File.Exists(frameworkListPath))
        {
            return null;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
        using var reader = XmlReader.Create(frameworkListPath, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        string? targetDirectory = document.Root?
            .Attribute("TargetFrameworkDirectory")?
            .Value;
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return null;
        }

        string normalizedTargetDirectory = targetDirectory
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        string resolvedDirectory = Path.GetFullPath(
            normalizedTargetDirectory,
            Path.GetDirectoryName(frameworkListPath)!);
        return File.Exists(Path.Join(resolvedDirectory, "mscorlib.dll"))
            ? resolvedDirectory
            : null;
    }
}
