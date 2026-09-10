namespace Csls.Support;

/// <summary>
/// Describes a validated graphical editor prerequisite request.
/// </summary>
internal sealed record GraphicalPrerequisiteOptions
{
    /// <summary>
    /// Gets the browser engines whose runtime dependencies are requested.
    /// </summary>
    internal required IReadOnlySet<string> WebBrowsers { get; init; }

    /// <summary>
    /// Gets whether to install and verify the tree-sitter CLI.
    /// </summary>
    internal bool InstallTreeSitter { get; init; }

    /// <summary>
    /// Gets whether to install and verify clipboard support.
    /// </summary>
    internal bool InstallClipboard { get; init; }

    /// <summary>
    /// Gets whether to install Vulkan rendering support.
    /// </summary>
    internal bool InstallVulkan { get; init; }

    /// <summary>
    /// Gets whether to provision only web browser dependencies.
    /// </summary>
    internal bool WebOnly { get; init; }

    /// <summary>
    /// Gets whether to extract clipboard and Vulkan packages into the tools directory.
    /// </summary>
    internal bool PortablePackages { get; init; }

    /// <summary>
    /// Gets whether to write a portable package cache key and exit.
    /// </summary>
    internal bool WritePortableCacheKey { get; init; }

    /// <summary>
    /// Gets the optional directory for verified APT package archives.
    /// </summary>
    internal string? PackageCachePath { get; init; }

    /// <summary>
    /// Gets whether to populate the package cache without installing dependencies.
    /// </summary>
    internal bool DownloadOnly { get; init; }

    /// <summary>
    /// Gets whether to write the web browser package cache key and exit.
    /// </summary>
    internal bool WritePackageCacheKey { get; init; }

    /// <summary>
    /// Gets whether to refresh the APT package index before provisioning.
    /// </summary>
    internal bool RefreshPackageIndex { get; init; }
}
