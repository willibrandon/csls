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
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Csls.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The csls repository root was not found.");
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
    internal static string ResolveTestProcessHost(string repositoryRoot) => Path.Combine(
        repositoryRoot,
        "artifacts",
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
        OperatingSystem.IsWindows() ? "hx.exe" : "hx",
        "hx");

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
        OperatingSystem.IsWindows() ? "fresh.exe" : "fresh",
        "fresh");

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
        OperatingSystem.IsWindows() ? "nvim.exe" : "nvim",
        "nvim");

    private static string Resolve(
        string repositoryRoot,
        string environmentVariable,
        string toolName,
        string version,
        string platform,
        string executableName,
        string fallback)
    {
        string? configuredPath = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        string installationPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "tools",
            toolName,
            version,
            platform);
        string? provisionedPath = Directory.Exists(installationPath)
            ? Directory
                .EnumerateFiles(installationPath, executableName, SearchOption.AllDirectories)
                .SingleOrDefault()
            : null;
        return provisionedPath ?? fallback;
    }

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
