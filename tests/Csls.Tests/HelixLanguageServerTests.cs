using System.Diagnostics;
using System.Runtime.InteropServices;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;

namespace Csls.Tests;

/// <summary>
/// Verifies csls behavior through a real Helix process running in a Hex1b PTY.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class HelixLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Opens a real C# project in Helix and displays Roslyn hover information from csls.
    /// </summary>
    [TestMethod]
    public async Task HelixDisplaysHoverFromCsls()
    {
        string repositoryRoot = FindRepositoryRoot();
        string helixPath = ResolveHelixExecutable(repositoryRoot);
        string workerPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        string fixturePath = Path.Combine(
            Path.GetTempPath(),
            $"csls-helix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string configurationRoot = Path.Combine(fixturePath, "config");
            string helixConfigurationPath = Path.Combine(configurationRoot, "helix");
            string workspaceConfigurationPath = Path.Combine(fixturePath, ".helix");
            string cachePath = Path.Combine(fixturePath, "cache");
            string dataPath = Path.Combine(fixturePath, "data");
            Directory.CreateDirectory(helixConfigurationPath);
            Directory.CreateDirectory(workspaceConfigurationPath);
            Directory.CreateDirectory(cachePath);
            Directory.CreateDirectory(dataPath);

            string projectPath = Path.Combine(fixturePath, "Fixture.csproj");
            string documentPath = Path.Combine(fixturePath, "Program.cs");
            string editorConfigurationPath = Path.Combine(
                helixConfigurationPath,
                "config.toml");
            string languageConfigurationPath = Path.Combine(
                workspaceConfigurationPath,
                "languages.toml");
            string logPath = Path.Combine(fixturePath, "helix.log");

            await File.WriteAllTextAsync(
                projectPath,
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                editorConfigurationPath,
                "theme = \"base16_default_dark\"\n",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                languageConfigurationPath,
                CreateLanguageConfiguration(workerPath),
                TestContext.CancellationToken).ConfigureAwait(false);

            string health = await RunHealthCheckAsync(
                helixPath,
                editorConfigurationPath,
                configurationRoot,
                cachePath,
                dataPath,
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("csls", health, StringComparison.OrdinalIgnoreCase);

            Hex1bTerminal terminal = Hex1bTerminal.CreateBuilder()
                .WithPtyProcess(options =>
                {
                    options.FileName = helixPath;
                    options.Arguments =
                    [
                        "--config",
                        editorConfigurationPath,
                        "--log",
                        logPath,
                        "-vvv",
                        "--working-dir",
                        fixturePath,
                        $"{documentPath}:7:10"
                    ];
                    options.WorkingDirectory = fixturePath;
                    options.Environment = new Dictionary<string, string>
                    {
                        ["XDG_CACHE_HOME"] = cachePath,
                        ["XDG_CONFIG_HOME"] = configurationRoot,
                        ["XDG_DATA_HOME"] = dataPath
                    };
                })
                .WithHeadless()
                .WithDimensions(120, 40)
                .Build();

            try
            {
                Task<int> runTask = terminal.RunAsync(TestContext.CancellationToken);
                Hex1bTerminalAutomator automator = new(
                    terminal,
                    defaultTimeout: TimeSpan.FromSeconds(60));

                await automator.WaitUntilAlternateScreenAsync().ConfigureAwait(false);
                await automator.WaitUntilTextAsync("Console.WriteLine").ConfigureAwait(false);
                await automator.SpaceAsync(TestContext.CancellationToken).ConfigureAwait(false);
                await automator.KeyAsync(Hex1bKey.K, TestContext.CancellationToken)
                    .ConfigureAwait(false);
                await automator.WaitUntilAsync(
                    static snapshot =>
                        snapshot.ContainsText("System.Console") ||
                        snapshot.ContainsText("No configured language server supports hover"),
                    description: "Helix to display hover information or a concrete LSP error")
                    .ConfigureAwait(false);

                using Hex1bTerminalSnapshot snapshot = automator.CreateSnapshot();
                string interactionLog = File.Exists(logPath)
                    ? await File.ReadAllTextAsync(logPath, TestContext.CancellationToken)
                        .ConfigureAwait(false)
                    : string.Empty;
                Assert.Contains(
                    "System.Console",
                    snapshot.GetScreenText(),
                    $"{interactionLog}{Environment.NewLine}{snapshot.GetScreenText()}");

                await automator.EscapeAsync(TestContext.CancellationToken).ConfigureAwait(false);
                await automator.TypeAsync(":q!", TestContext.CancellationToken)
                    .ConfigureAwait(false);
                await automator.EnterAsync(TestContext.CancellationToken).ConfigureAwait(false);
                int exitCode = await runTask.WaitAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);

                string log = File.Exists(logPath)
                    ? await File.ReadAllTextAsync(logPath, TestContext.CancellationToken)
                        .ConfigureAwait(false)
                    : string.Empty;
                Assert.AreEqual(0, exitCode, log);
                Assert.Contains("csls", log, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await terminal.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private const string DocumentText = """
        namespace Fixture;

        public static class Program
        {
            public static void Main()
            {
                Console.WriteLine("hello");
            }
        }
        """;

    private static string CreateLanguageConfiguration(string workerPath)
    {
        string dotnetPath = ResolveExecutable("DOTNET_HOST_PATH", "dotnet");
        return $$"""
            [language-server.csls]
            command = "{{ToTomlString(dotnetPath)}}"
            args = ["{{ToTomlString(workerPath)}}"]
            timeout = 60

            [[language]]
            name = "c-sharp"
            language-servers = ["csls"]
            """;
    }

    private static async Task<string> RunHealthCheckAsync(
        string helixPath,
        string editorConfigurationPath,
        string configurationRoot,
        string cachePath,
        string dataPath,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = helixPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workspacePath
        };
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(editorConfigurationPath);
        startInfo.ArgumentList.Add("--health");
        startInfo.ArgumentList.Add("c-sharp");
        startInfo.Environment["XDG_CACHE_HOME"] = cachePath;
        startInfo.Environment["XDG_CONFIG_HOME"] = configurationRoot;
        startInfo.Environment["XDG_DATA_HOME"] = dataPath;

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Helix health check did not start.");
        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(
            cancellationToken);
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(
            cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string standardOutput = await standardOutputTask.ConfigureAwait(false);
        string standardError = await standardErrorTask.ConfigureAwait(false);
        return $"exit code: {process.ExitCode}{Environment.NewLine}{standardOutput}{standardError}";
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
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

    private static string ResolveExecutable(string environmentVariable, string fallback)
    {
        string? configuredPath = Environment.GetEnvironmentVariable(environmentVariable);
        return string.IsNullOrWhiteSpace(configuredPath) ? fallback : configuredPath;
    }

    private static string ResolveHelixExecutable(string repositoryRoot)
    {
        string? configuredPath = Environment.GetEnvironmentVariable("CSLS_HELIX_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        string platform = GetCurrentPlatform();
        string executableName = OperatingSystem.IsWindows() ? "hx.exe" : "hx";
        string provisionedPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "tools",
            "helix",
            "25.07.1",
            platform,
            executableName);
        return File.Exists(provisionedPath) ? provisionedPath : "hx";
    }

    private static string GetCurrentPlatform()
    {
        Architecture architecture = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsLinux())
        {
            return architecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        }

        if (OperatingSystem.IsMacOS())
        {
            return architecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        }

        return "win-x64";
    }

    private static string ToTomlString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}
