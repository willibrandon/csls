using Csls.Control;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies csls behavior through a real GNU Emacs Eglot session running in a Hex1b PTY.
/// </summary>
[TestClass]
public sealed class EmacsLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Navigates to a definition in the second of two sibling solutions through real Eglot.
    /// </summary>
    [TestMethod]
    public async Task EglotNavigatesInSecondSiblingSolution()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string emacsPath = EditorToolResolver.ResolveEmacs(repositoryRoot);
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
            $"csls-emacs-{Guid.NewGuid():N}");
        string socketDirectory = EditorToolResolver.ResolveIsolatedControlSocketDirectory(
            repositoryRoot);
        Directory.CreateDirectory(fixturePath);
        try
        {
            string alphaPath = Path.Join(fixturePath, "alpha");
            string betaPath = Path.Join(fixturePath, "beta");
            string homePath = Path.Join(fixturePath, "home");
            Directory.CreateDirectory(alphaPath);
            Directory.CreateDirectory(betaPath);
            Directory.CreateDirectory(homePath);
            await WriteProjectAsync(
                alphaPath,
                "Alpha",
                AlphaDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            string betaDocumentPath = await WriteProjectAsync(
                betaPath,
                "Beta",
                BetaDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            string targetPath = Path.Join(betaPath, "Target.cs");
            await File.WriteAllTextAsync(
                targetPath,
                TargetDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            string readyPath = Path.Join(fixturePath, "eglot-ready");
            string navigationPath = Path.Join(fixturePath, "eglot-navigation");
            string initializationPath = Path.Join(fixturePath, "init.el");
            await File.WriteAllTextAsync(
                initializationPath,
                CreateInitialization(
                    fixturePath,
                    launcherPath,
                    readyPath,
                    navigationPath,
                    targetPath),
                TestContext.CancellationToken).ConfigureAwait(false);

            List<string> arguments =
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
                "CSLS_WORKER_PATH",
                workerPath,
                "--environment",
                ControlEndpoint.SocketDirectoryEnvironmentVariable,
                socketDirectory,
                "--",
                emacsPath,
                "-nw",
                "-Q",
                "--load",
                initializationPath,
                "+7:30",
                betaDocumentPath
            ];

            var workload = new Hex1bPtyWorkload(
                EditorToolResolver.ResolveDotNetHost(),
                arguments,
                fixturePath,
                width: 120,
                height: 40);
            Hex1bTerminal terminal = Hex1bTerminal.CreateBuilder()
                .WithWorkload(workload)
                .WithHeadless()
                .WithDimensions(120, 40)
                .Build();
            ProcessExitObservation? serverExit = null;
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
                        await automator.WaitUntilTextAsync("Target.DefinitionMarker")
                            .ConfigureAwait(false);

                        try
                        {
                            await FileTextWaiter.WaitAsync(
                                readyPath,
                                "ready",
                                TimeSpan.FromSeconds(60),
                                TestContext.CancellationToken).ConfigureAwait(false);
                            int serverProcessId = (await ControlSessionWaiter.WaitForRunningAsync(
                                fixturePath,
                                TimeSpan.FromSeconds(60),
                                TestContext.CancellationToken,
                                socketDirectory: socketDirectory).ConfigureAwait(false)).ProcessId;
                            serverExit = ProcessExitWaiter.Observe(serverProcessId);
                        }
                        catch (TaskCanceledException exception)
                            when (!TestContext.CancellationToken.IsCancellationRequested)
                        {
                            using Hex1bTerminalSnapshot connectionSnapshot =
                                automator.CreateSnapshot();
                            throw new InvalidOperationException(
                                $"Eglot did not connect.{Environment.NewLine}" +
                                connectionSnapshot.GetScreenText(),
                                exception);
                        }

                        await TerminalInput.SendAltCharacterAsync(
                            terminal,
                            '.',
                            TestContext.CancellationToken).ConfigureAwait(false);
                        try
                        {
                            await FileTextWaiter.WaitAsync(
                                navigationPath,
                                "navigated",
                                TimeSpan.FromSeconds(60),
                                TestContext.CancellationToken).ConfigureAwait(false);
                        }
                        catch (TaskCanceledException exception)
                            when (!TestContext.CancellationToken.IsCancellationRequested)
                        {
                            using Hex1bTerminalSnapshot navigationSnapshot =
                                automator.CreateSnapshot();
                            throw new InvalidOperationException(
                                $"Eglot did not navigate.{Environment.NewLine}" +
                                navigationSnapshot.GetScreenText(),
                                exception);
                        }
                        await automator.WaitUntilTextAsync("Eglot reached the second solution")
                            .ConfigureAwait(false);
                        using Hex1bTerminalSnapshot snapshot = automator.CreateSnapshot();
                        screenText = snapshot.GetScreenText();
                        Assert.Contains(
                            "Eglot reached the second solution",
                            screenText,
                            StringComparison.Ordinal);

                        await automator.Ctrl().KeyAsync(
                            Hex1bKey.X,
                            TestContext.CancellationToken).ConfigureAwait(false);
                        await automator.Ctrl().KeyAsync(
                            Hex1bKey.C,
                            TestContext.CancellationToken).ConfigureAwait(false);
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(0, exitCode, screenText);
            }
            finally
            {
                await terminal.DisposeAsync().ConfigureAwait(false);
                await workload.DisposeAsync().ConfigureAwait(false);
                if (serverExit is ProcessExitObservation observation)
                {
                    await ProcessExitWaiter.WaitAsync(
                        observation,
                        TimeSpan.FromSeconds(10),
                        TestContext.CancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await Task.WhenAll(
                DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)),
                DirectoryReleaseWaiter.DeleteAsync(socketDirectory, TimeSpan.FromSeconds(10)))
                .ConfigureAwait(false);
        }
    }

    private static async Task<string> WriteProjectAsync(
        string directoryPath,
        string projectName,
        string documentText,
        CancellationToken cancellationToken)
    {
        string projectPath = Path.Join(directoryPath, $"{projectName}.csproj");
        string solutionPath = Path.Join(directoryPath, $"{projectName}.slnx");
        string documentPath = Path.Join(directoryPath, "Program.cs");
        await File.WriteAllTextAsync(
            projectPath,
            ProjectText,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            solutionPath,
            $"<Solution><Project Path=\"{projectName}.csproj\" /></Solution>",
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            documentPath,
            documentText,
            cancellationToken).ConfigureAwait(false);
        return documentPath;
    }

    private static string CreateInitialization(
        string workspacePath,
        string launcherPath,
        string readyPath,
        string navigationPath,
        string targetPath)
    {
        string workspaceRoot = Path.EndsInDirectorySeparator(workspacePath)
            ? workspacePath
            : workspacePath + Path.DirectorySeparatorChar;
        string dotnetPath = EditorToolResolver.ResolveDotNetHost();
        return $$"""
            (require 'cc-mode)
            (require 'eglot)
            (require 'project)
            (require 'xref)
            (setq inhibit-startup-screen t
                  make-backup-files nil
                  auto-save-default nil
                  eglot-autoshutdown t
                  eglot-events-buffer-config '(:size 200000 :format full))
            (add-to-list 'auto-mode-alist '("\\.cs\\'" . csharp-mode))
            (add-to-list 'eglot-server-programs
                         '((csharp-mode :language-id "csharp") .
                           ({{ToElispString(dotnetPath)}}
                            {{ToElispString(launcherPath)}}
                            "lsp")))
            (cl-defmethod project-root ((project (head csls-test-project)))
              (cdr project))
            (defun csls-test-project-finder (directory)
              (when (file-in-directory-p directory {{ToElispString(workspaceRoot)}})
                (cons 'csls-test-project {{ToElispString(workspaceRoot)}})))
            (add-hook 'project-find-functions #'csls-test-project-finder)
            (add-hook 'csharp-mode-hook #'eglot-ensure)
            (add-hook 'eglot-connect-hook
                      (lambda (_server)
                        (with-temp-file {{ToElispString(readyPath)}}
                          (insert "ready"))))
            (add-hook 'xref-after-jump-hook
                      (lambda ()
                        (when (and buffer-file-name
                                   (file-equal-p buffer-file-name
                                                 {{ToElispString(targetPath)}}))
                          (with-temp-file {{ToElispString(navigationPath)}}
                            (insert "navigated")))))
            (defun csls-test-shutdown ()
              (interactive)
              (eglot-shutdown-all)
              (kill-emacs 0))
            (global-set-key (kbd "C-x C-c") #'csls-test-shutdown)
            """;
    }

    private static string ToElispString(string value) =>
        JsonSerializer.Serialize(value.Replace('\\', '/'));

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private const string AlphaDocumentText = """
        namespace Alpha;

        public static class Program
        {
            public static void Main()
            {
                Console.WriteLine("alpha");
            }
        }
        """;

    private const string BetaDocumentText = """
        namespace Beta;

        public static class Program
        {
            public static void Main()
            {
                Console.WriteLine(Target.DefinitionMarker);
            }
        }
        """;

    private const string TargetDocumentText = """
        namespace Beta;

        public static class Target
        {
            public const string DefinitionMarker = "Eglot reached the second solution";
        }
        """;
}
