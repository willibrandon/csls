using System.CommandLine;
using System.CommandLine.Parsing;

namespace Csls.Support;

/// <summary>
/// Parses and validates graphical prerequisite requests before provisioning starts.
/// </summary>
internal sealed class GraphicalPrerequisiteCommand
{
    private readonly Option<bool> _allBrowsers = new("--with-web-browsers")
    {
        Description = "Install dependencies for Chromium, Firefox, and WebKit."
    };
    private readonly Option<string[]> _browsers = new("--web-browser")
    {
        Description = "Install dependencies for a browser; may be repeated.",
        Arity = ArgumentArity.OneOrMore,
        AllowMultipleArgumentsPerToken = false
    };
    private readonly Option<bool> _webOnly = new("--web-only")
    {
        Description = "Install only browser dependencies."
    };
    private readonly Option<bool> _withoutTreeSitter = new("--without-tree-sitter")
    {
        Description = "Do not install the tree-sitter CLI."
    };
    private readonly Option<bool> _withoutClipboard = new("--without-clipboard")
    {
        Description = "Do not install clipboard support."
    };
    private readonly Option<bool> _withoutVulkan = new("--without-vulkan")
    {
        Description = "Do not install Vulkan rendering support."
    };
    private readonly Option<bool> _portablePackages = new("--portable-packages")
    {
        Description = "Extract clipboard and Vulkan packages into the tools directory."
    };
    private readonly Option<bool> _writePortableCacheKey = new("--write-portable-cache-key")
    {
        Description = "Write the portable package cache key to GITHUB_OUTPUT and exit."
    };
    private readonly Option<string?> _packageCache = new("--package-cache")
    {
        Description = "Reuse verified APT archives in this directory.",
        Arity = ArgumentArity.ExactlyOne,
        HelpName = "path"
    };
    private readonly Option<bool> _downloadOnly = new("--download-only")
    {
        Description = "Download web browser packages into the cache without installing."
    };
    private readonly Option<bool> _writePackageCacheKey = new("--write-package-cache-key")
    {
        Description = "Write the web browser package cache key to GITHUB_OUTPUT and exit."
    };
    private readonly Option<bool> _refreshPackageIndex = new("--refresh-package-index")
    {
        Description = "Refresh APT metadata before provisioning browser packages."
    };

    /// <summary>
    /// Creates the prerequisite command with side-effect-free argument validation.
    /// </summary>
    internal static RootCommand Create() => new GraphicalPrerequisiteCommand().CreateRoot();

    private RootCommand CreateRoot()
    {
        var command = new RootCommand(
            "Installs Linux runtime packages used by graphical and web editor tests.")
        {
            _allBrowsers, _browsers, _webOnly, _withoutTreeSitter, _withoutClipboard,
            _withoutVulkan, _portablePackages, _writePortableCacheKey, _packageCache,
            _downloadOnly, _writePackageCacheKey, _refreshPackageIndex
        };
        _browsers.AcceptOnlyFromAmong("chromium", "firefox", "webkit");
        command.Validators.Add(Validate);
        command.SetAction((result, _) =>
            GraphicalPrerequisiteInstaller.RunAsync(ReadOptions(result.CommandResult)));
        return command;
    }

    private GraphicalPrerequisiteOptions ReadOptions(CommandResult result)
    {
        var browsers = new HashSet<string>(result.GetValue(_browsers) ?? [], StringComparer.Ordinal);
        if (result.GetValue(_allBrowsers))
        {
            browsers.UnionWith(["chromium", "firefox", "webkit"]);
        }

        return new GraphicalPrerequisiteOptions
        {
            WebBrowsers = browsers,
            InstallTreeSitter = !result.GetValue(_withoutTreeSitter),
            InstallClipboard = !result.GetValue(_withoutClipboard),
            InstallVulkan = !result.GetValue(_withoutVulkan),
            WebOnly = result.GetValue(_webOnly),
            PortablePackages = result.GetValue(_portablePackages),
            WritePortableCacheKey = result.GetValue(_writePortableCacheKey),
            PackageCachePath = result.GetValue(_packageCache),
            DownloadOnly = result.GetValue(_downloadOnly),
            WritePackageCacheKey = result.GetValue(_writePackageCacheKey),
            RefreshPackageIndex = result.GetValue(_refreshPackageIndex)
        };
    }

    private void Validate(CommandResult result)
    {
        if (result.Children.Any(static child => child.Errors.Any()))
        {
            return;
        }

        bool webOnly = result.GetValue(_webOnly);
        bool hasBrowsers = result.GetValue(_allBrowsers) || result.GetResult(_browsers) is not null;
        if (webOnly && !hasBrowsers)
        {
            result.AddError("--web-only requires --web-browser or --with-web-browsers.");
        }

        bool hasPackageCache = result.GetResult(_packageCache) is not null;
        bool downloadOnly = result.GetValue(_downloadOnly);
        bool writePackageCacheKey = result.GetValue(_writePackageCacheKey);
        bool usesPackageCache = hasPackageCache || downloadOnly ||
            writePackageCacheKey || result.GetValue(_refreshPackageIndex);
        if (usesPackageCache && (!webOnly || result.GetValue(_portablePackages) || result.GetValue(_writePortableCacheKey)))
        {
            result.AddError("Browser package caching requires --web-only and cannot use portable packages.");
        }

        if (downloadOnly && (!hasPackageCache || writePackageCacheKey))
        {
            result.AddError("--download-only requires --package-cache and cannot use --write-package-cache-key.");
        }
    }
}
