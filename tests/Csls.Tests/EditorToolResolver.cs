using System.Runtime.InteropServices;

namespace Csls.Tests;

/// <summary>
/// Resolves repository-provisioned editor integration test executables.
/// </summary>
internal static class EditorToolResolver
{
    private static int s_controlSocketDirectorySequence;

    /// <summary>
    /// Finds the csls repository root from the compiled test assembly location.
    /// </summary>
    /// <returns>The absolute repository root.</returns>
    internal static string FindRepositoryRoot()
    {
        string? configuredPath = Environment.GetEnvironmentVariable("CSLS_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string configuredRoot = Path.GetFullPath(configuredPath);
            if (File.Exists(Path.Join(configuredRoot, "Csls.slnx")))
            {
                return configuredRoot;
            }

            throw new DirectoryNotFoundException(
                $"CSLS_REPOSITORY_ROOT does not contain Csls.slnx: {configuredRoot}");
        }

        string? repositoryRoot = FindRepositoryRoot(Environment.CurrentDirectory) ??
            FindRepositoryRoot(AppContext.BaseDirectory);
        return repositoryRoot ??
            throw new DirectoryNotFoundException("The csls repository root was not found.");
    }

    /// <summary>
    /// Resolves the configured build artifact root for the active test environment.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The absolute build artifact root.</returns>
    internal static string ResolveArtifactsRoot(string repositoryRoot)
    {
        string? configuredPath = Environment.GetEnvironmentVariable("CSLS_ARTIFACTS_ROOT");
        return string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Join(repositoryRoot, "artifacts")
            : Path.GetFullPath(configuredPath);
    }

    /// <summary>
    /// Resolves a short unique control-socket directory for one real editor test.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The absolute isolated control-socket directory.</returns>
    internal static string ResolveIsolatedControlSocketDirectory(string repositoryRoot)
    {
        int sequence = Interlocked.Increment(ref s_controlSocketDirectorySequence);
        return Path.Join(
            ResolveArtifactsRoot(repositoryRoot),
            "s",
            $"{Environment.ProcessId:x}-{sequence:x}");
    }

    /// <summary>
    /// Resolves the built csls launcher used by editor integration tests.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The absolute managed launcher assembly path.</returns>
    internal static string ResolveLauncher(string repositoryRoot) => Path.Join(
        ResolveArtifactsRoot(repositoryRoot),
        "bin",
        "Csls.App",
        "debug",
        "csls.dll");

    /// <summary>
    /// Resolves the built language-server worker supervised by the csls launcher.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The absolute managed worker assembly path.</returns>
    internal static string ResolveServerWorker(string repositoryRoot) => Path.Join(
        ResolveArtifactsRoot(repositoryRoot),
        "bin",
        "Csls.Worker",
        "debug",
        "csls-worker.dll");

    private static string? FindRepositoryRoot(string startingPath)
    {
        DirectoryInfo? directory = new(startingPath);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Resolves the active .NET host selected by the test platform.
    /// </summary>
    /// <returns>The configured host path or normal dotnet command name.</returns>
    internal static string ResolveDotNetHost()
    {
        string? configuredPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(configuredPath) ? "dotnet" : configuredPath;
    }

    /// <summary>
    /// Resolves the active .NET host to an absolute path for editor configuration.
    /// </summary>
    /// <returns>The absolute .NET host path.</returns>
    internal static string ResolveAbsoluteDotNetHost()
    {
        string configuredPath = ResolveDotNetHost();
        if (Path.IsPathFullyQualified(configuredPath))
        {
            return configuredPath;
        }

        string executableName = OperatingSystem.IsWindows()
            ? $"{configuredPath}.exe"
            : configuredPath;
        string? resolvedPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Join(directory.Trim('"'), executableName))
            .FirstOrDefault(File.Exists);
        return resolvedPath ?? throw new FileNotFoundException(
            $"The .NET host was not found on PATH: {configuredPath}");
    }

    /// <summary>
    /// Resolves a verified VS Code extension package provisioned for editor testing.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <param name="toolName">The provisioned extension tool name.</param>
    /// <param name="platformSpecific">Whether the package targets the active platform.</param>
    /// <returns>The absolute VSIX package path.</returns>
    internal static string ResolveVsCodeExtension(
        string repositoryRoot,
        string toolName,
        bool platformSpecific)
    {
        string? configuredToolsRoot = Environment.GetEnvironmentVariable("CSLS_TOOLS_ROOT");
        string toolsRoot = string.IsNullOrWhiteSpace(configuredToolsRoot)
            ? Path.Join(repositoryRoot, "artifacts", "tools")
            : Path.GetFullPath(configuredToolsRoot);
        string toolRoot = Path.Join(toolsRoot, toolName);
        string? package = Directory.Exists(toolRoot)
            ? Directory
                .EnumerateDirectories(toolRoot)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .Select(versionPath => Path.Join(
                    versionPath,
                    platformSpecific ? GetVsCodeTargetPlatform() : "all"))
                .Where(Directory.Exists)
                .SelectMany(path => Directory.EnumerateFiles(path, "*.vsix"))
                .FirstOrDefault()
            : null;
        return package is not null
            ? package
            : throw new FileNotFoundException(
                $"The current {toolName} extension is not provisioned. " +
                "Run scripts/Provision-VsCode.cs.");
    }

    /// <summary>
    /// Resolves the stable-channel VS Code server root used by remote extension-host tests.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The absolute VS Code server root.</returns>
    internal static string ResolveVsCodeRemoteServerRoot(string repositoryRoot)
    {
        string? configuredPath = Environment.GetEnvironmentVariable(
            "CSLS_VSCODE_REMOTE_SERVER_ROOT");
        string serverRoot;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            serverRoot = Path.GetFullPath(configuredPath);
        }
        else
        {
            string? configuredToolsRoot = Environment.GetEnvironmentVariable(
                "CSLS_TOOLS_ROOT");
            string toolsRoot = string.IsNullOrWhiteSpace(configuredToolsRoot)
                ? Path.Join(repositoryRoot, "artifacts", "tools")
                : Path.GetFullPath(configuredToolsRoot);
            string toolRoot = Path.Join(toolsRoot, "vscode-server");
            serverRoot = Directory.Exists(toolRoot)
                ? Directory
                    .EnumerateDirectories(toolRoot)
                    .OrderByDescending(Directory.GetLastWriteTimeUtc)
                    .Select(versionPath => Path.Join(versionPath, "linux-x64"))
                    .FirstOrDefault(path =>
                        File.Exists(Path.Join(path, "node")) &&
                        File.Exists(Path.Join(path, "out", "server-main.js"))) ??
                    Path.Join(toolRoot, "unavailable")
                : Path.Join(toolRoot, "unavailable");
        }

        return File.Exists(Path.Join(serverRoot, "node")) &&
            File.Exists(Path.Join(serverRoot, "out", "server-main.js"))
            ? serverRoot
            : throw new DirectoryNotFoundException(
                "The VS Code remote server is not provisioned. " +
                "Run scripts/Provision-VsCodeRemoteServer.cs.");
    }

    private static string GetVsCodeTargetPlatform()
    {
        string platform = GetPlatform(allowWindowsArm64: true, detectMusl: true);
        return platform switch
        {
            "linux-musl-arm64" => "alpine-arm64",
            "linux-musl-x64" => "alpine-x64",
            "osx-arm64" => "darwin-arm64",
            "osx-x64" => "darwin-x64",
            "win-arm64" => "win32-arm64",
            "win-x64" => "win32-x64",
            _ => platform
        };
    }

    /// <summary>
    /// Resolves the built C# process host used to provide deterministic child environments.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The absolute process host assembly path.</returns>
    internal static string ResolveTestProcessHost(string repositoryRoot) => Path.Join(
        ResolveArtifactsRoot(repositoryRoot),
        "bin",
        "Csls.TestProcessHost",
        "debug",
        "csls-test-process-host.dll");

    /// <summary>
    /// Resolves the provisioned Helix executable with an explicit override and installed fallback.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The executable path or command name.</returns>
    internal static string ResolveHelix(string repositoryRoot) => Resolve(
        repositoryRoot,
        "CSLS_HELIX_PATH",
        "helix",
        GetPlatform(allowWindowsArm64: false, detectMusl: false),
        OperatingSystem.IsWindows() ? "hx.exe" : "hx");

    /// <summary>
    /// Resolves the provisioned Fresh executable with an explicit override and installed fallback.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The executable path or command name.</returns>
    internal static string ResolveFresh(string repositoryRoot) => Resolve(
        repositoryRoot,
        "CSLS_FRESH_PATH",
        "fresh",
        GetPlatform(allowWindowsArm64: true, detectMusl: true),
        OperatingSystem.IsWindows() ? "fresh.exe" : "fresh");

    /// <summary>
    /// Resolves the provisioned Neovim executable with an explicit override and installed fallback.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The executable path or command name.</returns>
    internal static string ResolveNeovim(string repositoryRoot) => Resolve(
        repositoryRoot,
        "CSLS_NEOVIM_PATH",
        "neovim",
        GetPlatform(allowWindowsArm64: true, detectMusl: false),
        OperatingSystem.IsWindows() ? "nvim.exe" : "nvim");

    /// <summary>
    /// Resolves the provisioned GNU Emacs executable and its Eglot runtime environment.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The GNU Emacs executable path.</returns>
    internal static string ResolveEmacs(string repositoryRoot)
    {
        string? configuredPath = Environment.GetEnvironmentVariable("CSLS_EMACS_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        return Resolve(
            repositoryRoot,
            "CSLS_EMACS_PATH",
            "emacs",
            GetPlatform(allowWindowsArm64: true, detectMusl: true),
            OperatingSystem.IsWindows() ? "emacs.exe" : "emacs");
    }

    /// <summary>
    /// Resolves the provisioned Zed executable used by the graphical editor integration test.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The absolute Zed executable path.</returns>
    internal static string ResolveZed(string repositoryRoot) => Resolve(
        repositoryRoot,
        "CSLS_ZED_PATH",
        "zed",
        GetPlatform(allowWindowsArm64: false, detectMusl: false),
        "zed");

    /// <summary>
    /// Resolves the built csls extension package used by the Zed integration test.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The absolute built extension directory.</returns>
    internal static string ResolveCslsZedExtension(string repositoryRoot)
    {
        string? configuredPath = Environment.GetEnvironmentVariable(
            "CSLS_ZED_EXTENSION_PATH");
        string extensionPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Join(ResolveArtifactsRoot(repositoryRoot), "editors", "zed", "csls")
            : Path.GetFullPath(configuredPath);
        return File.Exists(Path.Join(extensionPath, "extension.toml")) &&
            File.Exists(Path.Join(extensionPath, "extension.wasm"))
            ? extensionPath
            : throw new DirectoryNotFoundException(
                "The csls Zed extension is not built. " +
                "Run scripts/Build-ZedExtension.cs.");
    }

    /// <summary>
    /// Resolves the current official C# extension used by the Zed integration test.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The absolute extracted extension directory.</returns>
    internal static string ResolveZedCSharpExtension(string repositoryRoot)
    {
        string? configuredPath = Environment.GetEnvironmentVariable(
            "CSLS_ZED_CSHARP_EXTENSION_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        string? configuredToolsRoot = Environment.GetEnvironmentVariable("CSLS_TOOLS_ROOT");
        string toolsRoot = string.IsNullOrWhiteSpace(configuredToolsRoot)
            ? Path.Join(repositoryRoot, "artifacts", "tools")
            : Path.GetFullPath(configuredToolsRoot);
        string toolRoot = Path.Join(toolsRoot, "zed-csharp-extension");
        string? extensionPath = Directory.Exists(toolRoot)
            ? Directory
                .EnumerateDirectories(toolRoot)
                .Select(versionPath => (
                    Path: versionPath,
                    Version: Version.TryParse(Path.GetFileName(versionPath), out Version? parsed)
                        ? parsed
                        : new Version()))
                .OrderByDescending(static candidate => candidate.Version)
                .Select(static candidate => Path.Join(candidate.Path, "all"))
                .FirstOrDefault(path => File.Exists(Path.Join(path, "extension.toml")))
            : null;
        return extensionPath is not null
            ? extensionPath
            : throw new DirectoryNotFoundException(
                "The Zed C# extension is not provisioned. " +
                "Run scripts/Provision-Zed.cs.");
    }

    /// <summary>
    /// Resolves the current upstream csharp-ls executable used as a parity oracle.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The absolute provisioned oracle path.</returns>
    internal static string ResolveCsharpLsOracle(string repositoryRoot) => Resolve(
        repositoryRoot,
        "CSLS_CSHARP_LS_ORACLE_PATH",
        "csharp-ls-oracle",
        GetPlatform(allowWindowsArm64: true, detectMusl: false),
        OperatingSystem.IsWindows() ? "csharp-ls.exe" : "csharp-ls");

    private static string Resolve(
        string repositoryRoot,
        string environmentVariable,
        string toolName,
        string platform,
        string executableName)
    {
        string? configuredPath = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        string? configuredToolsRoot = Environment.GetEnvironmentVariable("CSLS_TOOLS_ROOT");
        string toolsRoot = string.IsNullOrWhiteSpace(configuredToolsRoot)
            ? Path.Join(repositoryRoot, "artifacts", "tools")
            : Path.GetFullPath(configuredToolsRoot);
        string toolRoot = Path.Join(toolsRoot, toolName);
        string? provisionedPath = Directory.Exists(toolRoot)
            ? Directory
                .EnumerateDirectories(toolRoot)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .Select(versionPath => Path.Join(versionPath, platform))
                .Where(Directory.Exists)
                .SelectMany(path => Directory.EnumerateFiles(
                    path,
                    executableName,
                    SearchOption.AllDirectories))
                .FirstOrDefault()
            : null;
        return provisionedPath ?? throw new FileNotFoundException(
            $"The {toolName} executable is not provisioned. " +
            $"Run scripts/Provision-{GetProvisionerName(toolName)}.cs.");
    }

    private static string GetProvisionerName(string toolName) => toolName switch
    {
        "csharp-ls-oracle" => "CsharpLsOracle",
        "emacs" => "Emacs",
        "fresh" => "Fresh",
        "helix" => "Helix",
        "neovim" => "Neovim",
        "zed" => "Zed",
        _ => throw new ArgumentOutOfRangeException(nameof(toolName), toolName, null)
    };

    private static string GetPlatform(bool allowWindowsArm64, bool detectMusl)
    {
        Architecture architecture = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsLinux() && architecture == Architecture.X64)
        {
            return detectMusl && File.Exists("/etc/alpine-release")
                ? "linux-musl-x64"
                : "linux-x64";
        }

        if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64)
        {
            return detectMusl && File.Exists("/etc/alpine-release")
                ? "linux-musl-arm64"
                : "linux-arm64";
        }

        if (OperatingSystem.IsMacOS() && architecture == Architecture.X64)
        {
            return "osx-x64";
        }

        if (OperatingSystem.IsMacOS() && architecture == Architecture.Arm64)
        {
            return "osx-arm64";
        }

        if (OperatingSystem.IsWindows() && architecture == Architecture.X64)
        {
            return "win-x64";
        }

        if (OperatingSystem.IsWindows() && architecture == Architecture.Arm64)
        {
            return allowWindowsArm64 ? "win-arm64" : "win-x64";
        }

        throw new PlatformNotSupportedException(
            $"No editor test binary is available for {RuntimeInformation.OSDescription} {architecture}.");
    }
}
