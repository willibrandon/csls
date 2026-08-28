using System.Runtime.InteropServices;

namespace Csls.Tests;

/// <summary>
/// Resolves repository-provisioned editor integration test executables.
/// </summary>
internal static class EditorToolResolver
{
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
    /// <param name="version">The pinned extension version.</param>
    /// <param name="platformSpecific">Whether the package targets the active platform.</param>
    /// <returns>The absolute VSIX package path.</returns>
    internal static string ResolveVsCodeExtension(
        string repositoryRoot,
        string toolName,
        string version,
        bool platformSpecific)
    {
        string? configuredToolsRoot = Environment.GetEnvironmentVariable("CSLS_TOOLS_ROOT");
        string toolsRoot = string.IsNullOrWhiteSpace(configuredToolsRoot)
            ? Path.Join(repositoryRoot, "artifacts", "tools")
            : Path.GetFullPath(configuredToolsRoot);
        string extensionRoot = Path.Join(
            toolsRoot,
            toolName,
            version,
            platformSpecific ? GetVsCodeTargetPlatform() : "all");
        string[] packages = Directory.Exists(extensionRoot)
            ? [.. Directory.EnumerateFiles(extensionRoot, "*.vsix")]
            : [];
        return packages.Length == 1
            ? packages[0]
            : throw new FileNotFoundException(
                $"The pinned {toolName} {version} extension is not provisioned. " +
                "Run scripts/Provision-VsCode.cs.");
    }

    /// <summary>
    /// Resolves the pinned VS Code server root used by remote extension-host tests.
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
            serverRoot = Path.Join(
                toolsRoot,
                "vscode-server",
                "1.135.0",
                "linux-x64");
        }

        return File.Exists(Path.Join(serverRoot, "node")) &&
            File.Exists(Path.Join(serverRoot, "out", "server-main.js"))
            ? serverRoot
            : throw new DirectoryNotFoundException(
                "The pinned VS Code remote server is not provisioned. " +
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
    /// Resolves the pinned Helix executable with an explicit override and installed fallback.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The executable path or command name.</returns>
    internal static string ResolveHelix(string repositoryRoot) => Resolve(
        repositoryRoot,
        "CSLS_HELIX_PATH",
        "helix",
        "25.07.1",
        GetPlatform(allowWindowsArm64: false, detectMusl: false),
        OperatingSystem.IsWindows() ? "hx.exe" : "hx");

    /// <summary>
    /// Resolves the pinned Fresh executable with an explicit override and installed fallback.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The executable path or command name.</returns>
    internal static string ResolveFresh(string repositoryRoot) => Resolve(
        repositoryRoot,
        "CSLS_FRESH_PATH",
        "fresh",
        "0.4.10",
        GetPlatform(allowWindowsArm64: true, detectMusl: true),
        OperatingSystem.IsWindows() ? "fresh.exe" : "fresh");

    /// <summary>
    /// Resolves the pinned Neovim executable with an explicit override and installed fallback.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The executable path or command name.</returns>
    internal static string ResolveNeovim(string repositoryRoot) => Resolve(
        repositoryRoot,
        "CSLS_NEOVIM_PATH",
        "neovim",
        "0.12.5",
        GetPlatform(allowWindowsArm64: true, detectMusl: false),
        OperatingSystem.IsWindows() ? "nvim.exe" : "nvim");

    /// <summary>
    /// Resolves the pinned GNU Emacs executable and its Eglot runtime environment.
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
            "31.1",
            GetPlatform(allowWindowsArm64: true, detectMusl: true),
            OperatingSystem.IsWindows() ? "emacs.exe" : "emacs");
    }

    /// <summary>
    /// Resolves the pinned Zed executable used by the graphical editor integration test.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The absolute Zed executable path.</returns>
    internal static string ResolveZed(string repositoryRoot) => Resolve(
        repositoryRoot,
        "CSLS_ZED_PATH",
        "zed",
        "1.17.2",
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
    /// Resolves the pinned official C# extension used by the Zed integration test.
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
        string extensionPath = Path.Join(
            toolsRoot,
            "zed-csharp-extension",
            "1.2.2",
            "all");
        return File.Exists(Path.Join(extensionPath, "extension.toml"))
            ? extensionPath
            : throw new DirectoryNotFoundException(
                "The pinned Zed C# extension is not provisioned. " +
                "Run scripts/Provision-Zed.cs.");
    }

    /// <summary>
    /// Resolves the pinned upstream csharp-ls executable used as a parity oracle.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <returns>The absolute provisioned oracle path.</returns>
    internal static string ResolveCsharpLsOracle(string repositoryRoot) => Resolve(
        repositoryRoot,
        "CSLS_CSHARP_LS_ORACLE_PATH",
        "csharp-ls-oracle",
        "0.27.0",
        GetPlatform(allowWindowsArm64: true, detectMusl: false),
        OperatingSystem.IsWindows() ? "csharp-ls.exe" : "csharp-ls");

    private static string Resolve(
        string repositoryRoot,
        string environmentVariable,
        string toolName,
        string version,
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
        string installationPath = Path.Join(
            toolsRoot,
            toolName,
            version,
            platform);
        string? provisionedPath = Directory.Exists(installationPath)
            ? Directory
                .EnumerateFiles(installationPath, executableName, SearchOption.AllDirectories)
                .SingleOrDefault()
            : null;
        return provisionedPath ?? throw new FileNotFoundException(
            $"The pinned {toolName} {version} executable is not provisioned. " +
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
