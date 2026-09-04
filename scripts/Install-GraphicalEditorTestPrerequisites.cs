#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:property RootNamespace=Csls
#:package SharpCompress
#:include ScriptSupport.cs
#:include Support/AptPackageCache.cs
#:include Support/ProcessOutputCapture.cs

using Csls.Support;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Installs Linux runtime packages used by graphical and web editor tests.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Install-GraphicalEditorTestPrerequisites.cs " +
        "[--with-web-browsers] [--web-only] " +
        "[--web-browser <chromium|firefox|webkit>] " +
        "[--without-clipboard] [--without-tree-sitter] [--without-vulkan] " +
        "[--portable-packages] [--write-portable-cache-key] " +
        "[--package-cache <path>] [--download-only] [--write-package-cache-key] " +
        "[--refresh-package-index]")
        .ConfigureAwait(false);
    return 0;
}

var webBrowsers = new HashSet<string>(StringComparer.Ordinal);
bool installTreeSitter = true;
bool installClipboard = true;
bool installVulkan = true;
bool webOnly = false;
bool portablePackages = false;
bool writePortableCacheKey = false;
string? packageCachePath = null;
bool downloadOnly = false;
bool writePackageCacheKey = false;
bool refreshPackageIndex = false;
bool hasInvalidArguments = false;
for (int index = 0; index < args.Length; index++)
{
    if (string.Equals(args[index], "--with-web-browsers", StringComparison.Ordinal))
    {
        webBrowsers.UnionWith(["chromium", "firefox", "webkit"]);
        continue;
    }

    if (string.Equals(args[index], "--web-browser", StringComparison.Ordinal) &&
        index + 1 < args.Length &&
        args[index + 1] is "chromium" or "firefox" or "webkit")
    {
        webBrowsers.Add(args[++index]);
        continue;
    }

    if (string.Equals(args[index], "--web-only", StringComparison.Ordinal))
    {
        webOnly = true;
        continue;
    }

    if (string.Equals(args[index], "--without-tree-sitter", StringComparison.Ordinal))
    {
        installTreeSitter = false;
        continue;
    }

    if (string.Equals(args[index], "--without-clipboard", StringComparison.Ordinal))
    {
        installClipboard = false;
        continue;
    }

    if (string.Equals(args[index], "--without-vulkan", StringComparison.Ordinal))
    {
        installVulkan = false;
        continue;
    }

    if (string.Equals(args[index], "--portable-packages", StringComparison.Ordinal))
    {
        portablePackages = true;
        continue;
    }

    if (string.Equals(args[index], "--write-portable-cache-key", StringComparison.Ordinal))
    {
        writePortableCacheKey = true;
        continue;
    }

    if (string.Equals(args[index], "--package-cache", StringComparison.Ordinal) &&
        index + 1 < args.Length && !args[index + 1].StartsWith('-'))
    {
        packageCachePath = Path.GetFullPath(args[++index]);
        continue;
    }

    if (string.Equals(args[index], "--download-only", StringComparison.Ordinal))
    {
        downloadOnly = true;
        continue;
    }

    if (string.Equals(args[index], "--write-package-cache-key", StringComparison.Ordinal))
    {
        writePackageCacheKey = true;
        continue;
    }

    if (string.Equals(args[index], "--refresh-package-index", StringComparison.Ordinal))
    {
        refreshPackageIndex = true;
        continue;
    }

    hasInvalidArguments = true;
    break;
}

if (webOnly && webBrowsers.Count == 0)
{
    hasInvalidArguments = true;
}

if ((packageCachePath is not null || downloadOnly || writePackageCacheKey || refreshPackageIndex) &&
    (!webOnly || portablePackages || writePortableCacheKey ||
        (downloadOnly && (packageCachePath is null || writePackageCacheKey))))
{
    hasInvalidArguments = true;
}

if (hasInvalidArguments)
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Install-GraphicalEditorTestPrerequisites.cs " +
        "[--with-web-browsers] [--web-only] " +
        "[--web-browser <chromium|firefox|webkit>] " +
        "[--without-clipboard] [--without-tree-sitter] [--without-vulkan] " +
        "[--portable-packages] [--write-portable-cache-key] " +
        "[--package-cache <path>] [--download-only] [--write-package-cache-key] " +
        "[--refresh-package-index]")
        .ConfigureAwait(false);
    return 2;
}

try
{
    if (!OperatingSystem.IsLinux())
    {
        await Console.Out.WriteLineAsync(
            "Graphical editor test prerequisites are supplied by this platform.")
            .ConfigureAwait(false);
        return 0;
    }

    if (!File.Exists("/etc/debian_version"))
    {
        throw new PlatformNotSupportedException(
            "Automatic graphical editor test provisioning supports Debian and Ubuntu.");
    }

    if (writePortableCacheKey)
    {
        await WritePortableCacheKeyAsync(installClipboard, installVulkan)
            .ConfigureAwait(false);
        return 0;
    }

    if (refreshPackageIndex)
    {
        await RunPrivilegedAsync("apt-get", ["update", "--error-on=any"])
            .ConfigureAwait(false);
    }

    if (writePackageCacheKey || packageCachePath is not null)
    {
        IReadOnlyList<string> packages = await ResolveWebBrowserPackagesAsync(webBrowsers)
            .ConfigureAwait(false);
        if (writePackageCacheKey)
        {
            await AptPackageCache.WriteCacheKeyAsync(packages).ConfigureAwait(false);
        }
        else if (packageCachePath is not null)
        {
            await AptPackageCache.ProvisionAsync(packages, packageCachePath, downloadOnly)
                .ConfigureAwait(false);
        }

        return 0;
    }

    string? portableXclipPath = null;
    if (!webOnly)
    {
        IReadOnlyList<string> graphicalPackages = await ResolveGraphicalPackagesAsync(
            installClipboard,
            installVulkan).ConfigureAwait(false);
        if (portablePackages)
        {
            await InstallPackagesAsync(graphicalPackages.Where(static packageName =>
                packageName is not "xclip" and not "mesa-vulkan-drivers").ToArray())
                .ConfigureAwait(false);
            (portableXclipPath, string? vulkanLibraryPath, string? vulkanManifestPath) =
                await ProvisionPortablePackagesAsync(installClipboard, installVulkan)
                    .ConfigureAwait(false);
            await ExportPortableEnvironmentAsync(
                portableXclipPath,
                vulkanLibraryPath,
                vulkanManifestPath).ConfigureAwait(false);
        }
        else
        {
            await InstallPackagesAsync(graphicalPackages).ConfigureAwait(false);
        }
    }
    if (installTreeSitter && !webOnly)
    {
        string cargoHome = Environment.GetEnvironmentVariable("CARGO_HOME") ?? Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cargo");
        string treeSitterPath = Path.Join(
            cargoHome,
            "bin",
            OperatingSystem.IsWindows() ? "tree-sitter.exe" : "tree-sitter");
        bool hasTreeSitter = File.Exists(treeSitterPath) &&
            (await RunAsync(treeSitterPath, ["--version"]).ConfigureAwait(false)).ExitCode == 0;
        if (!hasTreeSitter)
        {
            string platform = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                _ => throw new PlatformNotSupportedException(
                    "Automatic tree-sitter provisioning supports Linux x64 and arm64.")
            };
            string repositoryRoot = FindRepositoryRoot();
            string toolsRoot = ScriptSupport.ResolveToolsRoot(repositoryRoot);
            (string tag, string assetName, Uri source, string expectedSha256) =
                await ScriptSupport.ResolveLatestGitHubReleaseAssetAsync(
                    "tree-sitter",
                    "tree-sitter",
                    name => string.Equals(
                        name,
                        $"tree-sitter-cli-{platform}.zip",
                        StringComparison.Ordinal),
                    CancellationToken.None).ConfigureAwait(false);
            string version = tag.TrimStart('v');
            string provisionedPath = await ScriptSupport.ProvisionArchiveToolAsync(
                toolsRoot,
                "tree-sitter",
                version,
                platform,
                source,
                assetName,
                expectedSha256,
                "tree-sitter",
                installationRootLevels: 0,
                versionArguments: ["--version"],
                expectedVersionText: $"tree-sitter {version}",
                CancellationToken.None).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(treeSitterPath)!);
            File.Delete(treeSitterPath);
            File.CreateSymbolicLink(treeSitterPath, provisionedPath);
        }

        await RunCheckedAsync(treeSitterPath, ["--version"]).ConfigureAwait(false);
    }
    if (!webOnly)
    {
        if (installClipboard)
        {
            await RunCheckedAsync(portableXclipPath ?? "xclip", ["-version"])
                .ConfigureAwait(false);
        }
        if (!Directory.Exists("/tmp/.X11-unix"))
        {
            await RunPrivilegedAsync(
                "mkdir",
                ["--parents", "--mode", "1777", "/tmp/.X11-unix"]).ConfigureAwait(false);
        }
    }
    if (webBrowsers.Count > 0)
    {
        await InstallPackagesAsync(
            await ResolveWebBrowserPackagesAsync(webBrowsers).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    return 0;
}
catch (Exception exception) when (exception is
    HttpRequestException or
    IOException or
    InvalidDataException or
    InvalidOperationException or
    PlatformNotSupportedException or
    UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static string FindRepositoryRoot()
{
    DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("The csls repository root was not found.");
}

static async Task<IReadOnlyList<string>> ResolveGraphicalPackagesAsync(
    bool installClipboard,
    bool installVulkan)
{
    string alsaPackage = await SelectPackageAsync("libasound2t64", "libasound2")
        .ConfigureAwait(false);
    string atkBridgePackage = await SelectPackageAsync(
        "libatk-bridge2.0-0t64",
        "libatk-bridge2.0-0").ConfigureAwait(false);
    string atkPackage = await SelectPackageAsync("libatk1.0-0t64", "libatk1.0-0")
        .ConfigureAwait(false);
    string atSpiPackage = await SelectPackageAsync("libatspi2.0-0t64", "libatspi2.0-0")
        .ConfigureAwait(false);
    string cupsPackage = await SelectPackageAsync("libcups2t64", "libcups2")
        .ConfigureAwait(false);
    string glibPackage = await SelectPackageAsync("libglib2.0-0t64", "libglib2.0-0")
        .ConfigureAwait(false);
    string gtkPackage = await SelectPackageAsync("libgtk-3-0t64", "libgtk-3-0")
        .ConfigureAwait(false);
    List<string> packages =
    [
        alsaPackage,
        atkBridgePackage,
        atkPackage,
        atSpiPackage,
        "libcairo2",
        cupsPackage,
        "libdbus-1-3",
        "libdrm2",
        "libfontconfig1",
        "libgbm1",
        glibPackage,
        gtkPackage,
        "libnspr4",
        "libnss3",
        "libpango-1.0-0",
        "libudev1",
        "libx11-6",
        "libxcb1",
        "libxcomposite1",
        "libxdamage1",
        "libxext6",
        "libxfixes3",
        "libxkbcommon0",
        "libxrandr2",
        "libxshmfence1",
        "libxss1",
        "libxtst6",
        "xauth",
        "xvfb"
    ];
    if (installClipboard)
    {
        packages.Add("xclip");
    }
    if (installVulkan)
    {
        packages.Add("libvulkan1");
        packages.Add("mesa-vulkan-drivers");
    }

    return packages;
}

static async Task<IReadOnlyList<string>> ResolveWebBrowserPackagesAsync(
    IReadOnlySet<string> webBrowsers)
{
    string alsaPackage = await SelectPackageAsync("libasound2t64", "libasound2")
        .ConfigureAwait(false);
    string atkBridgePackage = await SelectPackageAsync(
        "libatk-bridge2.0-0t64",
        "libatk-bridge2.0-0").ConfigureAwait(false);
    string atkPackage = await SelectPackageAsync("libatk1.0-0t64", "libatk1.0-0")
        .ConfigureAwait(false);
    string atSpiPackage = await SelectPackageAsync("libatspi2.0-0t64", "libatspi2.0-0")
        .ConfigureAwait(false);
    string cupsPackage = await SelectPackageAsync("libcups2t64", "libcups2")
        .ConfigureAwait(false);
    string glibPackage = await SelectPackageAsync("libglib2.0-0t64", "libglib2.0-0")
        .ConfigureAwait(false);
    var packages = new HashSet<string>(StringComparer.Ordinal);
    if (webBrowsers.Contains("chromium"))
    {
        packages.UnionWith(
        [
            alsaPackage,
            atkBridgePackage,
            atkPackage,
            atSpiPackage,
            "libcairo2",
            cupsPackage,
            "libdbus-1-3",
            "libdrm2",
            "libgbm1",
            glibPackage,
            "libnspr4",
            "libnss3",
            "libpango-1.0-0",
            "libx11-6",
            "libxcb1",
            "libxcomposite1",
            "libxdamage1",
            "libxext6",
            "libxfixes3",
            "libxkbcommon0",
            "libxrandr2"
        ]);
    }

    if (webBrowsers.Contains("firefox"))
    {
        string gtkPackage = await SelectPackageAsync("libgtk-3-0t64", "libgtk-3-0")
            .ConfigureAwait(false);
        packages.UnionWith(
        [
            alsaPackage,
            atkPackage,
            "libcairo-gobject2",
            "libcairo2",
            "libdbus-1-3",
            "libfontconfig1",
            "libfreetype6",
            "libgdk-pixbuf-2.0-0",
            glibPackage,
            gtkPackage,
            "libpango-1.0-0",
            "libpangocairo-1.0-0",
            "libx11-6",
            "libx11-xcb1",
            "libxcb-shm0",
            "libxcb1",
            "libxcomposite1",
            "libxcursor1",
            "libxdamage1",
            "libxext6",
            "libxfixes3",
            "libxi6",
            "libxrandr2",
            "libxrender1"
        ]);
    }

    if (webBrowsers.Contains("webkit"))
    {
        string avifPackage = await SelectMatchingPackageAsync("^libavif[0-9]+$")
            .ConfigureAwait(false);
        string eventPackage = await SelectPackageAsync(
            "libevent-2.1-7t64",
            "libevent-2.1-7").ConfigureAwait(false);
        string icuPackage = await SelectMatchingPackageAsync("^libicu[0-9]+$")
            .ConfigureAwait(false);
        string gtkPackage = await SelectMatchingPackageAsync("^libgtk-4-[0-9]+$")
            .ConfigureAwait(false);
        string jpegPackage = await SelectPackageAsync("libjpeg-turbo8", "libjpeg62-turbo")
            .ConfigureAwait(false);
        string jxlPackage = await SelectMatchingPackageAsync("^libjxl[0-9]+([.][0-9]+)?$")
            .ConfigureAwait(false);
        string pngPackage = await SelectPackageAsync("libpng16-16t64", "libpng16-16")
            .ConfigureAwait(false);
        string pslPackage = await SelectPackageAsync("libpsl5t64", "libpsl5")
            .ConfigureAwait(false);
        string webpPackage = await SelectMatchingPackageAsync("^libwebp[0-9]+$")
            .ConfigureAwait(false);
        string x264Package = await SelectMatchingPackageAsync("^libx264-[0-9]+$")
            .ConfigureAwait(false);
        packages.UnionWith(
        [
            avifPackage,
            icuPackage,
            "libatomic1",
            atkBridgePackage,
            atkPackage,
            "libbrotli1",
            "libcairo2",
            "libdrm2",
            "libenchant-2-2",
            "libepoxy0",
            eventPackage,
            "libflite1",
            "libfontconfig1",
            "libfreetype6",
            "libgbm1",
            "libgcrypt20",
            "libgles2",
            glibPackage,
            "libgpg-error0",
            "libgstreamer-gl1.0-0",
            "libgstreamer-plugins-bad1.0-0",
            "libgstreamer-plugins-base1.0-0",
            "libgstreamer1.0-0",
            "libgraphene-1.0-0",
            "libharfbuzz-icu0",
            "libharfbuzz0b",
            "libhyphen0",
            gtkPackage,
            jpegPackage,
            jxlPackage,
            "liblcms2-2",
            "libmanette-0.2-0",
            "libnghttp2-14",
            "libopus0",
            "libpango-1.0-0",
            pngPackage,
            pslPackage,
            "libsoup-3.0-0",
            "libsqlite3-0",
            "libsecret-1-0",
            "libsystemd0",
            "libtasn1-6",
            "libwayland-client0",
            "libwayland-egl1",
            "libwayland-server0",
            webpPackage,
            "libwebpdemux2",
            "libxkbcommon0",
            "libxml2",
            "libxslt1.1",
            x264Package,
            "zlib1g"
        ]);
    }

    return packages.Order(StringComparer.Ordinal).ToArray();
}

static async Task WritePortableCacheKeyAsync(bool installClipboard, bool installVulkan)
{
    string githubOutputPath = Environment.GetEnvironmentVariable("GITHUB_OUTPUT")
        ?? throw new InvalidOperationException(
            "GITHUB_OUTPUT is required to write the portable package cache key.");
    var keyParts = new List<string>
    {
        await ReadRequiredOutputAsync("dpkg", ["--print-architecture"])
            .ConfigureAwait(false),
        await File.ReadAllTextAsync("/etc/os-release").ConfigureAwait(false)
    };
    if (installClipboard)
    {
        keyParts.Add(await ResolveCandidateVersionAsync("xclip").ConfigureAwait(false));
    }
    if (installVulkan)
    {
        keyParts.Add(await ResolveCandidateVersionAsync("mesa-vulkan-drivers")
            .ConfigureAwait(false));
    }

    byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', keyParts)));
    await File.AppendAllTextAsync(
        githubOutputPath,
        $"key={Convert.ToHexStringLower(digest)}{Environment.NewLine}").ConfigureAwait(false);
}

static async Task<string> ResolveCandidateVersionAsync(string packageName)
{
    string output = await ReadRequiredOutputAsync("apt-cache", ["policy", packageName])
        .ConfigureAwait(false);
    const string candidatePrefix = "Candidate:";
    string? version = output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(static line => line.Trim())
        .Where(static line => line.StartsWith(candidatePrefix, StringComparison.Ordinal))
        .Select(static line => line[candidatePrefix.Length..].Trim())
        .SingleOrDefault();
    return !string.IsNullOrEmpty(version) &&
        !string.Equals(version, "(none)", StringComparison.Ordinal)
        ? $"{packageName}={version}"
        : throw new InvalidOperationException(
            $"No installation candidate is available for {packageName}.");
}

static async Task<(string? XclipPath, string? VulkanLibraryPath, string? VulkanManifestPath)>
    ProvisionPortablePackagesAsync(bool installClipboard, bool installVulkan)
{
    string repositoryRoot = FindRepositoryRoot();
    string toolsRoot = ScriptSupport.ResolveToolsRoot(repositoryRoot);
    string portableRoot = Path.Join(toolsRoot, "graphical-editor-prerequisites");
    string xclipPath = Path.Join(portableRoot, "bin", "xclip");
    string vulkanLibraryPath = Path.Join(portableRoot, "lib", "libvulkan_lvp.so");
    string vulkanManifestPath = Path.Join(portableRoot, "share", "lvp_icd.json");
    if ((!installClipboard || File.Exists(xclipPath)) &&
        (!installVulkan || File.Exists(vulkanLibraryPath) && File.Exists(vulkanManifestPath)))
    {
        return (
            installClipboard ? xclipPath : null,
            installVulkan ? vulkanLibraryPath : null,
            installVulkan ? vulkanManifestPath : null);
    }

    string stagingRoot = Path.Join(
        toolsRoot,
        $".graphical-editor-prerequisites-{Guid.NewGuid():N}");
    string extractedRoot = Path.Join(stagingRoot, "extracted");
    Directory.CreateDirectory(extractedRoot);
    try
    {
        var packageNames = new List<string>();
        if (installClipboard)
        {
            packageNames.Add("xclip");
        }
        if (installVulkan)
        {
            packageNames.Add("mesa-vulkan-drivers");
        }

        await RunCheckedAsync(
            "apt-get",
            ["download", .. packageNames],
            stagingRoot).ConfigureAwait(false);
        foreach (string archivePath in Directory.EnumerateFiles(stagingRoot, "*.deb"))
        {
            await RunCheckedAsync(
                "dpkg-deb",
                ["--extract", archivePath, extractedRoot]).ConfigureAwait(false);
        }

        if (Directory.Exists(portableRoot))
        {
            Directory.Delete(portableRoot, recursive: true);
        }
        if (installClipboard)
        {
            string extractedXclipPath = FindRequiredFile(extractedRoot, "xclip");
            Directory.CreateDirectory(Path.GetDirectoryName(xclipPath)!);
            File.Copy(extractedXclipPath, xclipPath);
            await RunCheckedAsync("chmod", ["755", xclipPath]).ConfigureAwait(false);
        }
        if (installVulkan)
        {
            string extractedVulkanLibraryPath = FindRequiredFile(
                extractedRoot,
                "libvulkan_lvp.so");
            string extractedVulkanManifestPath = Directory
                .EnumerateFiles(extractedRoot, "lvp_icd*.json", SearchOption.AllDirectories)
                .Single();
            Directory.CreateDirectory(Path.GetDirectoryName(vulkanLibraryPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(vulkanManifestPath)!);
            File.Copy(extractedVulkanLibraryPath, vulkanLibraryPath);
            File.Copy(extractedVulkanManifestPath, vulkanManifestPath);
        }
    }
    finally
    {
        Directory.Delete(stagingRoot, recursive: true);
    }

    return (
        installClipboard ? xclipPath : null,
        installVulkan ? vulkanLibraryPath : null,
        installVulkan ? vulkanManifestPath : null);
}

static string FindRequiredFile(string rootPath, string fileName) =>
    Directory.EnumerateFiles(rootPath, fileName, SearchOption.AllDirectories).Single();

static async Task ExportPortableEnvironmentAsync(
    string? xclipPath,
    string? vulkanLibraryPath,
    string? vulkanManifestPath)
{
    string githubEnvironmentPath = Environment.GetEnvironmentVariable("GITHUB_ENV")
        ?? throw new InvalidOperationException(
            "GITHUB_ENV is required to export portable package paths.");
    string githubPath = Environment.GetEnvironmentVariable("GITHUB_PATH")
        ?? throw new InvalidOperationException(
            "GITHUB_PATH is required to export the portable xclip path.");
    if (xclipPath is not null)
    {
        await File.AppendAllTextAsync(
            githubPath,
            $"{Path.GetDirectoryName(xclipPath)}{Environment.NewLine}").ConfigureAwait(false);
    }
    if (vulkanLibraryPath is not null && vulkanManifestPath is not null)
    {
        string libraryDirectory = Path.GetDirectoryName(vulkanLibraryPath)!;
        string? currentLibraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        string libraryPath = string.IsNullOrEmpty(currentLibraryPath)
            ? libraryDirectory
            : $"{libraryDirectory}:{currentLibraryPath}";
        await File.AppendAllTextAsync(
            githubEnvironmentPath,
            $"LD_LIBRARY_PATH={libraryPath}{Environment.NewLine}" +
            $"VK_DRIVER_FILES={vulkanManifestPath}{Environment.NewLine}")
            .ConfigureAwait(false);
    }
}

static async Task<string> ReadRequiredOutputAsync(
    string executablePath,
    IReadOnlyList<string> arguments)
{
    (int exitCode, string output, string error) = await RunAsync(executablePath, arguments)
        .ConfigureAwait(false);
    if (exitCode != 0)
    {
        throw new InvalidOperationException(error.Trim());
    }

    return output.Trim();
}

static async Task InstallPackagesAsync(IReadOnlyList<string> packages)
{
    if (await ArePackagesInstalledAsync(packages).ConfigureAwait(false))
    {
        return;
    }

    string[] installArguments =
    [
        "install",
        "--yes",
        "--no-install-recommends",
        .. packages
    ];
    if (!await TryRunPrivilegedAsync("apt-get", installArguments).ConfigureAwait(false))
    {
        await RunPrivilegedAsync("apt-get", ["update"]).ConfigureAwait(false);
        await RunPrivilegedAsync("apt-get", installArguments).ConfigureAwait(false);
    }
}

static Task RunPrivilegedAsync(string executablePath, IReadOnlyList<string> arguments) =>
    string.Equals(Environment.UserName, "root", StringComparison.Ordinal)
        ? RunCheckedAsync(executablePath, arguments)
        : RunCheckedAsync("sudo", ["--non-interactive", executablePath, .. arguments]);

static async Task<bool> TryRunPrivilegedAsync(
    string executablePath,
    IReadOnlyList<string> arguments)
{
    (string actualExecutablePath, IReadOnlyList<string> actualArguments) =
        string.Equals(Environment.UserName, "root", StringComparison.Ordinal)
            ? (executablePath, arguments)
            : ("sudo", ["--non-interactive", executablePath, .. arguments]);
    (int exitCode, _, _) = await RunAsync(
        actualExecutablePath,
        actualArguments,
        streamOutput: true).ConfigureAwait(false);
    return exitCode == 0;
}

static async Task RunCheckedAsync(
    string executablePath,
    IReadOnlyList<string> arguments,
    string? workingDirectory = null)
{
    (int exitCode, _, _) = await RunAsync(
        executablePath,
        arguments,
        workingDirectory,
        streamOutput: true).ConfigureAwait(false);
    if (exitCode != 0)
    {
        throw new InvalidOperationException(
            $"{executablePath} failed with exit code {exitCode}.");
    }
}

static async Task<bool> PackageExistsAsync(string packageName)
{
    (int exitCode, string output, _) = await RunAsync(
        "apt-cache",
        ["show", "--no-all-versions", packageName]).ConfigureAwait(false);
    return exitCode == 0 && !string.IsNullOrWhiteSpace(output);
}

static async Task<bool> ArePackagesInstalledAsync(IReadOnlyList<string> packageNames)
{
    (int exitCode, string output, _) = await RunAsync(
        "dpkg-query",
        [
            "--show",
            "--showformat=${binary:Package}\\t${db:Status-Abbrev}\\n",
            .. packageNames
        ]).ConfigureAwait(false);
    return exitCode == 0 && output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .All(static line => line.EndsWith("\tii ", StringComparison.Ordinal));
}

static async Task<string> SelectPackageAsync(params string[] packageNames)
{
    foreach (string packageName in packageNames)
    {
        if (await PackageExistsAsync(packageName).ConfigureAwait(false))
        {
            return packageName;
        }
    }

    throw new InvalidOperationException(
        $"None of the required packages are available: {string.Join(", ", packageNames)}.");
}

static async Task<string> SelectMatchingPackageAsync(string packageNamePattern)
{
    (int exitCode, string output, string error) = await RunAsync(
        "apt-cache",
        ["search", "--names-only", packageNamePattern]).ConfigureAwait(false);
    if (exitCode != 0)
    {
        throw new InvalidOperationException(error.Trim());
    }

    string? packageName = output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(static line => line.Split(" - ", 2, StringSplitOptions.None)[0])
        .Where(name => Regex.IsMatch(name, packageNamePattern, RegexOptions.CultureInvariant))
        .OrderDescending(StringComparer.Ordinal)
        .FirstOrDefault();
    return packageName ?? throw new InvalidOperationException(
        $"No required package matches {packageNamePattern}.");
}

static async Task<(int ExitCode, string Output, string Error)> RunAsync(
    string executablePath,
    IReadOnlyList<string> arguments,
    string? workingDirectory = null,
    bool streamOutput = false)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = executablePath,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false
    };
    startInfo.WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
    startInfo.Environment["DEBIAN_FRONTEND"] = "noninteractive";
    foreach (string argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    if (streamOutput)
    {
        await Console.Error.WriteLineAsync($"Starting {executablePath} {string.Join(' ', arguments)}")
            .ConfigureAwait(false);
    }

    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"The process did not start: {executablePath}");
    Task<string> standardOutputTask = ProcessOutputCapture.ReadAsync(
        process.StandardOutput.BaseStream, process.StandardOutput.CurrentEncoding, streamOutput ? Console.Error : null);
    Task<string> standardErrorTask = ProcessOutputCapture.ReadAsync(
        process.StandardError.BaseStream, process.StandardError.CurrentEncoding, streamOutput ? Console.Error : null);
    await Task.WhenAll(standardOutputTask, standardErrorTask, process.WaitForExitAsync()).ConfigureAwait(false);
    string output = await standardOutputTask.ConfigureAwait(false);
    string error = await standardErrorTask.ConfigureAwait(false);
    return (process.ExitCode, output, error);
}
