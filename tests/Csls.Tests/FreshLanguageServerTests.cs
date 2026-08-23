using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies csls behavior through a real Fresh process running in a Hex1b PTY.
/// </summary>
[TestClass]
public sealed class FreshLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Opens a real C# file in Fresh and displays Roslyn hover information from csls.
    /// </summary>
    [TestMethod]
    public async Task FreshDisplaysHoverFromCsls()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string freshPath = EditorToolResolver.ResolveFresh(repositoryRoot);
        string processHostPath = EditorToolResolver.ResolveTestProcessHost(repositoryRoot);
        string workerPath = Path.Join(
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

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-fresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            string configurationPath = Path.Join(fixturePath, "fresh.json");
            string logPath = Path.Join(fixturePath, "fresh.log");
            string eventLogPath = Path.Join(fixturePath, "fresh-events.jsonl");
            string homePath = Path.Join(fixturePath, "home");
            string cachePath = Path.Join(fixturePath, "cache");
            string dataPath = Path.Join(fixturePath, "data");
            string statePath = Path.Join(fixturePath, "state");
            Directory.CreateDirectory(homePath);
            Directory.CreateDirectory(cachePath);
            Directory.CreateDirectory(dataPath);
            Directory.CreateDirectory(statePath);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                configurationPath,
                CreateConfiguration(workerPath),
                TestContext.CancellationToken).ConfigureAwait(false);

            var workload = new Hex1bPtyWorkload(
                EditorToolResolver.ResolveDotNetHost(),
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
                    fixturePath,
                    "--environment",
                    "XDG_DATA_HOME",
                    dataPath,
                    "--environment",
                    "XDG_STATE_HOME",
                    statePath,
                    "--",
                    freshPath,
                    "--config",
                    configurationPath,
                    "--log-file",
                    logPath,
                    "--event-log",
                    eventLogPath,
                    "--no-plugins",
                    "--no-init",
                    "--no-restore",
                    "--no-upgrade-check",
                    $"{documentPath}:7:10"
                ],
                fixturePath,
                width: 120,
                height: 40);
            Hex1bTerminal terminal = Hex1bTerminal.CreateBuilder()
                .WithWorkload(workload)
                .WithHeadless()
                .WithDimensions(120, 40)
                .Build();
            try
            {
                int exitCode = await workload.RunAsync(
                    terminal,
                    async () =>
                    {
                        Hex1bTerminalAutomator automator = new(
                            terminal,
                            defaultTimeout: TimeSpan.FromSeconds(60));
                        await automator.WaitUntilAlternateScreenAsync().ConfigureAwait(false);
                        await automator.WaitUntilTextAsync("Console.WriteLine").ConfigureAwait(false);
                        try
                        {
                            await ControlSessionWaiter.WaitForRunningAsync(
                                fixturePath,
                                TimeSpan.FromSeconds(60),
                                TestContext.CancellationToken).ConfigureAwait(false);
                            await automator.WaitUntilTextAsync("LSP (csharp) ready")
                                .ConfigureAwait(false);
                        }
                        catch (Exception exception)
                            when (exception is TimeoutException or Hex1bAutomationException)
                        {
                            string diagnostics = await ReadDiagnosticsAsync(
                                logPath,
                                eventLogPath,
                                statePath,
                                TestContext.CancellationToken).ConfigureAwait(false);
                            using Hex1bTerminalSnapshot snapshot = automator.CreateSnapshot();
                            throw new InvalidOperationException(
                                $"Fresh did not complete LSP initialization.{Environment.NewLine}" +
                                $"{diagnostics}{Environment.NewLine}{snapshot.GetScreenText()}",
                                exception);
                        }

                        await TerminalInput.SendAltCharacterAsync(
                            terminal,
                            'k',
                            TestContext.CancellationToken).ConfigureAwait(false);
                        try
                        {
                            await automator.WaitUntilTextAsync("System.Console").ConfigureAwait(false);
                        }
                        catch (Hex1bAutomationException)
                        {
                            string diagnostics = await ReadDiagnosticsAsync(
                                logPath,
                                eventLogPath,
                                statePath,
                                TestContext.CancellationToken).ConfigureAwait(false);
                            TestContext.WriteLine(diagnostics);
                            throw;
                        }

                        await automator.Ctrl().KeyAsync(Hex1bKey.Q, TestContext.CancellationToken)
                            .ConfigureAwait(false);
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                string log = await File.ReadAllTextAsync(
                    logPath,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(0, exitCode, log);
                Assert.Contains("Processing Hover request", log, StringComparison.Ordinal);
            }
            finally
            {
                await terminal.DisposeAsync().ConfigureAwait(false);
                await workload.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

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

    private static string CreateConfiguration(string workerPath)
    {
        string dotnetPath = EditorToolResolver.ResolveDotNetHost();
        return $$"""
            {
              "languages": {
                "csharp": {
                  "extensions": ["cs"],
                  "grammar": "c_sharp",
                  "comment_prefix": "//",
                  "auto_indent": true
                }
              },
              "lsp": {
                "csharp": {
                  "command": {{ToJsonString(dotnetPath)}},
                  "args": [{{ToJsonString(workerPath)}}],
                  "enabled": true
                }
              }
            }
            """;
    }

    private static string ToJsonString(string value) =>
        $"\"{JsonEncodedText.Encode(value)}\"";

    private static async Task<string> ReadDiagnosticsAsync(
        string logPath,
        string eventLogPath,
        string statePath,
        CancellationToken cancellationToken)
    {
        string log = File.Exists(logPath)
            ? await File.ReadAllTextAsync(logPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        string events = File.Exists(eventLogPath)
            ? await File.ReadAllTextAsync(eventLogPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        string languageServerLogDirectory = Path.Join(statePath, "fresh", "logs", "lsp");
        var languageServerLogs = new List<string>();
        if (Directory.Exists(languageServerLogDirectory))
        {
            foreach (string languageServerLogPath in Directory.EnumerateFiles(
                languageServerLogDirectory,
                "*",
                SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string languageServerLog = await File.ReadAllTextAsync(
                    languageServerLogPath,
                    cancellationToken).ConfigureAwait(false);
                languageServerLogs.Add(
                    $"{Path.GetRelativePath(statePath, languageServerLogPath)}:" +
                    $"{Environment.NewLine}{languageServerLog}");
            }
        }

        return $"Fresh log:{Environment.NewLine}{log}{Environment.NewLine}" +
            $"Fresh events:{Environment.NewLine}{events}{Environment.NewLine}" +
            $"Language server logs:{Environment.NewLine}" +
            string.Join(Environment.NewLine, languageServerLogs);
    }
}
