using Hex1b;
using Hex1b.Automation;
using System.Text.Json;

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
        string launcherPath = EditorToolResolver.ResolveLauncher(repositoryRoot);
        string workerPath = EditorToolResolver.ResolveServerWorker(repositoryRoot);
        Assert.IsTrue(File.Exists(launcherPath), $"Launcher not found at {launcherPath}.");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        Assert.IsTrue(
            File.Exists(processHostPath),
            $"Test process host not found at {processHostPath}.");

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-neovim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            string configurationPath = Path.Join(fixturePath, "init.lua");
            string readyPath = Path.Join(fixturePath, "lsp-ready");
            string hoverRequestedPath = Path.Join(fixturePath, "hover-requested");
            string homePath = Path.Join(fixturePath, "home");
            string cachePath = Path.Join(fixturePath, "cache");
            string configurationRoot = Path.Join(fixturePath, "config");
            string dataPath = Path.Join(fixturePath, "data");
            string statePath = Path.Join(fixturePath, "state");
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
                CreateConfiguration(launcherPath, readyPath, hoverRequestedPath),
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
                        "--environment",
                        "CSLS_WORKER_PATH",
                        workerPath,
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
            int? serverProcessId = null;
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
                        serverProcessId = (await ControlSessionWaiter.WaitForRunningAsync(
                            fixturePath,
                            TimeSpan.FromSeconds(60),
                            TestContext.CancellationToken).ConfigureAwait(false)).ProcessId;

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
                if (serverProcessId is int processId)
                {
                    await ProcessExitWaiter.WaitAsync(
                        processId,
                        TimeSpan.FromSeconds(10),
                        TestContext.CancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies the move-to-file refactoring through Neovim's native workspace-edit client.
    /// </summary>
    [TestMethod]
    public async Task NeovimMovesTypeToNewFile()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string neovimPath = EditorToolResolver.ResolveNeovim(repositoryRoot);
        string processHostPath = EditorToolResolver.ResolveTestProcessHost(repositoryRoot);
        string launcherPath = EditorToolResolver.ResolveLauncher(repositoryRoot);
        string workerPath = EditorToolResolver.ResolveServerWorker(repositoryRoot);
        Assert.IsTrue(File.Exists(launcherPath), $"Launcher not found at {launcherPath}.");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-neovim-move-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            string targetPath = Path.Join(fixturePath, "Helper.cs");
            string configurationPath = Path.Join(fixturePath, "init.lua");
            string readyPath = Path.Join(fixturePath, "lsp-ready");
            string appliedPath = Path.Join(fixturePath, "move-applied");
            string homePath = Path.Join(fixturePath, "home");
            string cachePath = Path.Join(fixturePath, "cache");
            string configurationRoot = Path.Join(fixturePath, "config");
            string dataPath = Path.Join(fixturePath, "data");
            string statePath = Path.Join(fixturePath, "state");
            Directory.CreateDirectory(homePath);
            Directory.CreateDirectory(cachePath);
            Directory.CreateDirectory(configurationRoot);
            Directory.CreateDirectory(dataPath);
            Directory.CreateDirectory(statePath);
            await File.WriteAllTextAsync(
                documentPath,
                MoveTypeDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                configurationPath,
                CreateMoveConfiguration(
                    launcherPath,
                    documentPath,
                    targetPath,
                    readyPath,
                    appliedPath),
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
                    "--environment",
                    "CSLS_WORKER_PATH",
                    workerPath,
                    "--",
                    neovimPath,
                    "-u",
                    configurationPath,
                    "-i",
                    "NONE",
                    "--noplugin",
                    "+call cursor(8, 24)",
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
            int? serverProcessId = null;
            try
            {
                int exitCode = await workload.RunAsync(
                    terminal,
                    async () =>
                    {
                        Hex1bTerminalAutomator automator = new(
                            terminal,
                            defaultTimeout: TimeSpan.FromSeconds(60));
                        await automator.WaitUntilTextAsync("class Helper").ConfigureAwait(false);
                        await FileTextWaiter.WaitAsync(
                            readyPath,
                            "ready",
                            TimeSpan.FromSeconds(60),
                            TestContext.CancellationToken).ConfigureAwait(false);
                        serverProcessId = (await ControlSessionWaiter.WaitForRunningAsync(
                            fixturePath,
                            TimeSpan.FromSeconds(60),
                            TestContext.CancellationToken).ConfigureAwait(false)).ProcessId;

                        string moveStatus = await FileTextWaiter.WaitForContentsAsync(
                            appliedPath,
                            TimeSpan.FromSeconds(60),
                            TestContext.CancellationToken).ConfigureAwait(false);
                        Assert.AreEqual("applied", moveStatus.Trim());

                        await automator.TypeAsync(":qa!", TestContext.CancellationToken)
                            .ConfigureAwait(false);
                        await automator.EnterAsync(TestContext.CancellationToken)
                            .ConfigureAwait(false);
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(0, exitCode);
            }
            finally
            {
                await terminal.DisposeAsync().ConfigureAwait(false);
                await workload.DisposeAsync().ConfigureAwait(false);
                if (serverProcessId is int processId)
                {
                    await ProcessExitWaiter.WaitAsync(
                        processId,
                        TimeSpan.FromSeconds(10),
                        TestContext.CancellationToken).ConfigureAwait(false);
                }
            }

            Assert.IsTrue(File.Exists(targetPath));
            Assert.Contains(
                "internal static class Helper",
                await File.ReadAllTextAsync(
                    targetPath,
                    TestContext.CancellationToken).ConfigureAwait(false),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "class Helper",
                await File.ReadAllTextAsync(
                    documentPath,
                    TestContext.CancellationToken).ConfigureAwait(false),
                StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
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

    private const string MoveTypeDocumentText = """
        namespace Fixture;

        public static class Program
        {
            public static int Read() => Helper.Value;
        }

        internal static class Helper
        {
            public static int Value => 42;
        }
        """;

    private static string CreateConfiguration(
        string launcherPath,
        string readyPath,
        string hoverRequestedPath)
    {
        string dotnetPath = EditorToolResolver.ResolveDotNetHost();
        return $$"""
            vim.lsp.log.set_level('off')
            local function when_workspace_ready(client, buffer, callback)
              local function poll()
                client:request('$/csharp/debugInfo', nil, function(error, result)
                  if not error and result and result.workspace and
                      result.workspace.phase == 'Ready' then
                    callback()
                    return
                  end
                  vim.defer_fn(poll, 50)
                end, buffer)
              end
              poll()
            end
            vim.api.nvim_create_autocmd('LspAttach', {
              callback = function(args)
                local client = vim.lsp.get_client_by_id(args.data.client_id)
                if client and client.name == 'csls' then
                  when_workspace_ready(client, args.buf, function()
                    vim.keymap.set('n', 'K', function()
                      vim.fn.writefile({ 'requested' }, {{ToLuaString(hoverRequestedPath)}})
                      vim.lsp.buf.hover()
                    end, { buffer = args.buf })
                    vim.fn.writefile({ 'ready' }, {{ToLuaString(readyPath)}})
                  end)
                end
              end,
            })
            vim.lsp.config('csls', {
              cmd = { {{ToLuaString(dotnetPath)}}, {{ToLuaString(launcherPath)}}, 'lsp' },
              filetypes = { 'cs' },
              root_dir = function(_, on_dir)
                on_dir(vim.fn.getcwd())
              end,
            })
            vim.lsp.enable('csls')
            """;
    }

    private static string CreateMoveConfiguration(
        string launcherPath,
        string documentPath,
        string targetPath,
        string readyPath,
        string appliedPath)
    {
        string dotnetPath = EditorToolResolver.ResolveDotNetHost();
        return $$"""
            vim.lsp.log.set_level('off')
            local capabilities = vim.lsp.protocol.make_client_capabilities()
            capabilities.workspace.didChangeWatchedFiles.dynamicRegistration = true
            local function when_workspace_ready(client, buffer, callback)
              local function poll()
                client:request('$/csharp/debugInfo', nil, function(error, result)
                  if not error and result and result.workspace and
                      result.workspace.phase == 'Ready' then
                    callback()
                    return
                  end
                  vim.defer_fn(poll, 50)
                end, buffer)
              end
              poll()
            end
            local function when_move_action_ready(client, buffer, callback)
              local function request_action()
                local params = vim.lsp.util.make_range_params(0, client.offset_encoding)
                params.context = {
                  diagnostics = {},
                  only = { 'refactor' },
                  triggerKind = vim.lsp.protocol.CodeActionTriggerKind.Invoked,
                }
                client:request('textDocument/codeAction', params, function(error, actions)
                  if not error then
                    for _, action in ipairs(actions or {}) do
                      if action.title == 'Move Helper to Helper.cs' and action.edit then
                        callback(action)
                        return
                      end
                    end
                  end
                  vim.defer_fn(request_action, 50)
                end, buffer)
              end
              request_action()
            end
            local function persist_move()
              vim.cmd('silent wall')
              local target_readable = vim.fn.filereadable({{ToLuaString(targetPath)}})
              local source_text = table.concat(
                vim.fn.readfile({{ToLuaString(documentPath)}}), '\n')
              local target_text = target_readable == 1 and table.concat(
                vim.fn.readfile({{ToLuaString(targetPath)}}), '\n') or ''
              local source_has_helper = string.find(
                source_text, 'class Helper', 1, true) ~= nil
              local target_has_helper = string.find(
                target_text, 'class Helper', 1, true) ~= nil
              if target_readable ~= 1 or source_has_helper or not target_has_helper then
                vim.fn.writefile({ string.format(
                  'failed: target_readable=%d source_has_helper=%s target_has_helper=%s',
                  target_readable,
                  tostring(source_has_helper),
                  tostring(target_has_helper)) }, {{ToLuaString(appliedPath)}})
                return
              end
              vim.fn.writefile({ 'applied' }, {{ToLuaString(appliedPath)}})
            end
            vim.api.nvim_create_autocmd('LspAttach', {
              callback = function(args)
                local client = vim.lsp.get_client_by_id(args.data.client_id)
                if client and client.name == 'csls' then
                  when_workspace_ready(client, args.buf, function()
                    when_move_action_ready(client, args.buf, function(action)
                      vim.fn.writefile({ 'ready' }, {{ToLuaString(readyPath)}})
                      local ok, error_message = pcall(function()
                        vim.lsp.util.apply_workspace_edit(
                          action.edit,
                          client.offset_encoding)
                        persist_move()
                      end)
                      if not ok then
                        vim.fn.writefile(
                          { 'failed: ' .. tostring(error_message) },
                          {{ToLuaString(appliedPath)}})
                      end
                    end)
                  end)
                end
              end,
            })
            vim.lsp.config('csls', {
              cmd = { {{ToLuaString(dotnetPath)}}, {{ToLuaString(launcherPath)}}, 'lsp' },
              capabilities = capabilities,
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
