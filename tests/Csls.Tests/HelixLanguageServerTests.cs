using System.Diagnostics;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;

namespace Csls.Tests;

/// <summary>
/// Verifies csls behavior through a real Helix process running in a Hex1b PTY.
/// </summary>
[TestClass]
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
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string helixPath = EditorToolResolver.ResolveHelix(repositoryRoot);
        string processHostPath = EditorToolResolver.ResolveTestProcessHost(repositoryRoot);
        string workerPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        Assert.IsTrue(
            File.Exists(processHostPath),
            $"Test process host not found at {processHostPath}.");

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
            string statePath = Path.Combine(fixturePath, "state");
            string homePath = Path.Combine(fixturePath, "home");
            Directory.CreateDirectory(helixConfigurationPath);
            Directory.CreateDirectory(workspaceConfigurationPath);
            Directory.CreateDirectory(cachePath);
            Directory.CreateDirectory(dataPath);
            Directory.CreateDirectory(statePath);
            Directory.CreateDirectory(homePath);

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
                statePath,
                homePath,
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("csls", health, StringComparison.OrdinalIgnoreCase);

            Hex1bTerminal terminal = Hex1bTerminal.CreateBuilder()
                .WithPtyProcess(options =>
                {
                    options.FileName = EditorToolResolver.ResolveDotNetHost();
                    options.Arguments =
                    [
                        processHostPath,
                        "--environment",
                        "TERM",
                        "xterm-256color",
                        "--environment",
                        "COLORTERM",
                        "truecolor",
                        "--environment",
                        "HOME",
                        homePath,
                        "--environment",
                        "XDG_CACHE_HOME",
                        cachePath,
                        "--environment",
                        "XDG_CONFIG_HOME",
                        configurationRoot,
                        "--environment",
                        "XDG_DATA_HOME",
                        dataPath,
                        "--environment",
                        "XDG_STATE_HOME",
                        statePath,
                        "--",
                        helixPath,
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
        string dotnetPath = EditorToolResolver.ResolveDotNetHost();
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
        string statePath,
        string homePath,
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
        startInfo.Environment["XDG_STATE_HOME"] = statePath;
        startInfo.Environment["HOME"] = homePath;

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

    private static string ToTomlString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}
