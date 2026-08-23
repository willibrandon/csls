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
        "0.12.4",
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
            "30.2",
            GetPlatform(allowWindowsArm64: true, detectMusl: true),
            OperatingSystem.IsWindows() ? "emacs.exe" : "emacs");
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
        "0.26.0",
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
