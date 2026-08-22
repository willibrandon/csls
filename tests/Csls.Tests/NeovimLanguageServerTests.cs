using System.Text.Json;
using Hex1b;
using Hex1b.Automation;

namespace Csls.Tests;

/// <summary>
/// Verifies csls behavior through a real Neovim process running in a Hex1b PTY.
/// </summary>
[TestClass]
public sealed class NeovimLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Opens a real C# file in Neovim and displays Roslyn hover information from csls.
    /// </summary>
    [TestMethod]
    public async Task NeovimDisplaysHoverFromCsls()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string neovimPath = EditorToolResolver.ResolveNeovim(repositoryRoot);
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
            $"csls-neovim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Combine(fixturePath, "Program.cs");
            string configurationPath = Path.Combine(fixturePath, "init.lua");
            string readyPath = Path.Combine(fixturePath, "lsp-ready");
            string hoverRequestedPath = Path.Combine(fixturePath, "hover-requested");
            string homePath = Path.Combine(fixturePath, "home");
            string cachePath = Path.Combine(fixturePath, "cache");
            string configurationRoot = Path.Combine(fixturePath, "config");
            string dataPath = Path.Combine(fixturePath, "data");
            string statePath = Path.Combine(fixturePath, "state");
            Directory.CreateDirectory(homePath);
            Directory.CreateDirectory(cachePath);
            Directory.CreateDirectory(configurationRoot);
            Directory.CreateDirectory(dataPath);
            Directory.CreateDirectory(statePath);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                configurationPath,
                CreateConfiguration(workerPath, readyPath, hoverRequestedPath),
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
                        configurationRoot,
                        "--environment",
                        "XDG_DATA_HOME",
                        dataPath,
                        "--environment",
                        "XDG_STATE_HOME",
                        statePath,
                        "--",
                        neovimPath,
                        "-u",
                        configurationPath,
                        "-i",
                        "NONE",
                        "--noplugin",
                        "+call cursor(7, 10)",
                    documentPath
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
                string screenText = string.Empty;
                int exitCode = await workload.RunAsync(
                    terminal,
                    async () =>
                    {
                        Hex1bTerminalAutomator automator = new(
                            terminal,
                            defaultTimeout: TimeSpan.FromSeconds(60));
                        await automator.WaitUntilTextAsync("Console.WriteLine").ConfigureAwait(false);
                        await FileTextWaiter.WaitAsync(
                            readyPath,
                            "ready",
                            TimeSpan.FromSeconds(60),
                            TestContext.CancellationToken).ConfigureAwait(false);

                        await automator.TypeAsync("K", TestContext.CancellationToken)
                            .ConfigureAwait(false);
                        await FileTextWaiter.WaitAsync(
                            hoverRequestedPath,
                            "requested",
                            TimeSpan.FromSeconds(60),
                            TestContext.CancellationToken).ConfigureAwait(false);
                        await automator.WaitUntilTextAsync(
                            "System.Console").ConfigureAwait(false);
                        using Hex1bTerminalSnapshot snapshot = automator.CreateSnapshot();
                        screenText = snapshot.GetScreenText();
                        Assert.Contains("System.Console", screenText);

                        await automator.TypeAsync(":qa!", TestContext.CancellationToken)
                            .ConfigureAwait(false);
                        await automator.EnterAsync(TestContext.CancellationToken).ConfigureAwait(false);
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(0, exitCode, screenText);
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

    private static string CreateConfiguration(
        string workerPath,
        string readyPath,
        string hoverRequestedPath)
    {
        string dotnetPath = EditorToolResolver.ResolveDotNetHost();
        return $$"""
            vim.lsp.log.set_level('trace')
            vim.api.nvim_create_autocmd('LspAttach', {
              callback = function(args)
                local client = vim.lsp.get_client_by_id(args.data.client_id)
                if client and client.name == 'csls' then
                  vim.keymap.set('n', 'K', function()
                    vim.fn.writefile({ 'requested' }, {{ToLuaString(hoverRequestedPath)}})
                    vim.lsp.buf.hover()
                  end, { buffer = args.buf })
                  vim.fn.writefile({ 'ready' }, {{ToLuaString(readyPath)}})
                end
              end,
            })
            vim.lsp.config('csls', {
              cmd = { {{ToLuaString(dotnetPath)}}, {{ToLuaString(workerPath)}} },
              filetypes = { 'cs' },
              root_dir = function(_, on_dir)
                on_dir(vim.fn.getcwd())
              end,
            })
            vim.lsp.enable('csls')
            """;
    }

    private static string ToLuaString(string value) =>
        $"\"{JsonEncodedText.Encode(value)}\"";
}
