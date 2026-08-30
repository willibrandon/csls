using Csls.Control;
using Csls.Control.Contracts;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Csls.Tests;

/// <summary>
/// Verifies the Hex1b dashboard against a real language-server process and control socket.
/// </summary>
[TestClass]
public sealed class DashboardLanguageServerTests
{
    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;
    private const string DocumentText = """
        Console.WriteLine(missingName);
        """;
    private const string DocsScreenshotPathEnvironmentVariable =
        "CSLS_DASHBOARD_DOCS_SCREENSHOT_PATH";

    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Navigates and refreshes real session state through the public dashboard command.
    /// </summary>
    [TestMethod]
    public async Task DashboardShowsAndRefreshesRealLanguageServerState()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string workerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        string cliPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.App",
            "debug",
            "csls.dll");
        string cliWorkerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Cli.Worker",
            "debug",
            "csls-cli-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        Assert.IsTrue(File.Exists(cliPath), $"CLI launcher not found at {cliPath}.");
        Assert.IsTrue(File.Exists(cliWorkerPath), $"CLI worker not found at {cliWorkerPath}.");

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-dashboard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "DashboardFixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-dashboard-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);
            ControlSessionInfo session = await ControlSessionWaiter.WaitForRunningAsync(
                fixturePath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);
            var control = new ControlRpcClient(session.SocketPath);
            await using ConfiguredAsyncDisposable controlCleanup = control.ConfigureAwait(false);
            ControlDashboardSnapshot workspaceSnapshot =
                await control.GetDashboardSnapshotAsync(
                    new ControlDashboardRequest { IncludeDiagnostics = true },
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                1,
                workspaceSnapshot.TotalDiagnostics,
                string.Join(
                    Environment.NewLine,
                    workspaceSnapshot.Diagnostics.Select(static diagnostic =>
                        $"{diagnostic.Id}: {diagnostic.FilePath}: {diagnostic.Message}")));
            Assert.AreEqual("CS0103", workspaceSnapshot.Diagnostics.Single().Id);

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CSLS_CLI_WORKER_PATH"] = cliWorkerPath,
                ["DOTNET_HOST_PATH"] = EditorToolResolver.ResolveDotNetHost()
            };
            const int width = 140;
            const int height = 35;
            var workload = new Hex1bPtyWorkload(
                EditorToolResolver.ResolveDotNetHost(),
                [
                    cliPath,
                    "dashboard",
                    "--session",
                    lsp.ProcessId.ToString(CultureInfo.InvariantCulture)
                ],
                fixturePath,
                width,
                height,
                environment);
            await using ConfiguredAsyncDisposable workloadCleanup = workload.ConfigureAwait(false);
            using Hex1bTerminal terminal = Hex1bTerminal.CreateBuilder()
                .WithWorkload(workload)
                .WithHeadless()
                .WithDimensions(width, height)
                .Build();

            int exitCode = await workload.RunAsync(
                terminal,
                async () =>
                {
                    var automator = new Hex1bTerminalAutomator(
                        terminal,
                        defaultTimeout: TimeSpan.FromSeconds(60));
                    await automator.WaitUntilAsync(
                        screen => screen.InAlternateScreen ||
                            screen.ContainsText("Unhandled exception"),
                        description: "dashboard startup to enter alternate screen or report an error")
                        .ConfigureAwait(false);
                    using (Hex1bTerminalSnapshot startupSnapshot = automator.CreateSnapshot())
                    {
                        Assert.IsTrue(
                            startupSnapshot.InAlternateScreen,
                            startupSnapshot.GetScreenText());
                    }

                    await automator.WaitUntilTextAsync("csls dashboard").ConfigureAwait(false);
                    await automator.WaitUntilTextAsync(
                        lsp.ProcessId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);

                    await automator.KeyAsync(
                        Hex1bKey.F9,
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("Run clear caches for the selected session?")
                        .ConfigureAwait(false);
                    await automator.EnterAsync(TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("Completed clear-cache").ConfigureAwait(false);
                    await automator.DownAsync(TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.DownAsync(TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("Workspaces").ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("MSBuildWorkspace").ConfigureAwait(false);
                    await automator.DownAsync(TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("DashboardFixture").ConfigureAwait(false);
                    await automator.DownAsync(TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("Program.cs").ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("yes").ConfigureAwait(false);
                    await automator.DownAsync(TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("CS0103").ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("Program.cs:1:").ConfigureAwait(false);
                    using (Hex1bTerminalSnapshot diagnosticsSnapshot =
                        automator.CreateSnapshot())
                    {
                        string? screenshotPath = Environment.GetEnvironmentVariable(
                            DocsScreenshotPathEnvironmentVariable);
                        if (!string.IsNullOrWhiteSpace(screenshotPath))
                        {
                            string svg = diagnosticsSnapshot.ToSvg(new TerminalSvgOptions
                            {
                                ShowCellGrid = false
                            });
                            await File.WriteAllTextAsync(
                                screenshotPath,
                                svg,
                                TestContext.CancellationToken).ConfigureAwait(false);
                        }
                    }

                    await automator.DownAsync(TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("Accepted:").ConfigureAwait(false);

                    using Hex1bTerminalSnapshot requestsSnapshot = automator.CreateSnapshot();
                    int acceptedBeforeRefresh = GetAcceptedRequestCount(
                        requestsSnapshot.GetScreenText());
                    await automator.KeyAsync(
                        Hex1bKey.F5,
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilAsync(
                        screen => GetAcceptedRequestCount(screen.GetScreenText()) > acceptedBeforeRefresh,
                        description: "dashboard refresh to schedule a new real inspection")
                        .ConfigureAwait(false);

                    await automator.DownAsync(TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("Foreground concurrency:").ConfigureAwait(false);
                    await automator.DownAsync(TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("MSBuildWorkspace").ConfigureAwait(false);
                    await automator.DownAsync(TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("semantic tokens").ConfigureAwait(false);
                    await automator.DownAsync(TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilTextAsync("C# workspace ready in ")
                        .ConfigureAwait(false);

                    await automator.Ctrl().KeyAsync(
                        Hex1bKey.C,
                        TestContext.CancellationToken).ConfigureAwait(false);
                },
                TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(0, exitCode);
            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Shows complete diagnostic locations and messages in a narrow real terminal.
    /// </summary>
    [TestMethod]
    public async Task DashboardShowsCompleteDiagnosticDetailsAtNarrowWidth()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string workerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        string cliPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.App",
            "debug",
            "csls.dll");
        string cliWorkerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Cli.Worker",
            "debug",
            "csls-cli-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        Assert.IsTrue(File.Exists(cliPath), $"CLI launcher not found at {cliPath}.");
        Assert.IsTrue(File.Exists(cliWorkerPath), $"CLI worker not found at {cliWorkerPath}.");

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-dashboard-narrow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            const string longFileName =
                "ThisIsAnIntentionallyLongDiagnosticFileNameForNarrowTerminals.cs";
            const string missingIdentifier =
                "thisIdentifierNameMustRemainVisibleInTheSelectedDiagnosticDetails";
            const string secondMissingIdentifier =
                "secondIdentifierMustBeSelectableWithTheMouseAndKeyboard";
            string documentText =
                $"Console.WriteLine({missingIdentifier});{Environment.NewLine}" +
                $"Console.WriteLine({secondMissingIdentifier});{Environment.NewLine}";
            string documentPath = Path.Join(
                fixturePath,
                "src",
                "Features",
                "Diagnostics",
                longFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(documentPath)!);
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "DashboardFixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                documentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-dashboard-narrow-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.CompleteInitializationAsync().ConfigureAwait(false);
            await lsp.OpenDocumentAsync(
                documentPath,
                documentText)
                .ConfigureAwait(false);
            await ControlSessionWaiter.WaitForRunningAsync(
                fixturePath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CSLS_CLI_WORKER_PATH"] = cliWorkerPath,
                ["DOTNET_HOST_PATH"] = EditorToolResolver.ResolveDotNetHost()
            };
            const int width = 110;
            const int height = 28;
            var workload = new Hex1bPtyWorkload(
                EditorToolResolver.ResolveDotNetHost(),
                [
                    cliPath,
                    "dashboard",
                    "--session",
                    lsp.ProcessId.ToString(CultureInfo.InvariantCulture)
                ],
                fixturePath,
                width,
                height,
                environment);
            await using ConfiguredAsyncDisposable workloadCleanup =
                workload.ConfigureAwait(false);
            using Hex1bTerminal terminal = Hex1bTerminal.CreateBuilder()
                .WithWorkload(workload)
                .WithHeadless()
                .WithDimensions(width, height)
                .Build();

            int exitCode = await workload.RunAsync(
                terminal,
                async () =>
                {
                    var automator = new Hex1bTerminalAutomator(
                        terminal,
                        defaultTimeout: TimeSpan.FromSeconds(60));
                    await automator.WaitUntilTextAsync("csls dashboard").ConfigureAwait(false);
                    await automator.WaitUntilAsync(
                        _ => workload.ContainsRawOutput("\u001b[?1003h"u8) &&
                            workload.ContainsRawOutput("\u001b[?1006h"u8),
                        timeout: TimeSpan.FromSeconds(5),
                        description: "real dashboard process to enable terminal mouse reporting")
                        .ConfigureAwait(false);
                    await automator.ClickAtAsync(
                        4,
                        8,
                        MouseButton.Left,
                        TestContext.CancellationToken).ConfigureAwait(false);

                    await automator.WaitUntilTextAsync("CS0103").ConfigureAwait(false);
                    using Hex1bTerminalSnapshot snapshot = automator.CreateSnapshot();
                    string screenText = snapshot.GetScreenText();
                    Assert.Contains(
                        $"{longFileName}:1:",
                        screenText,
                        StringComparison.Ordinal);
                    Assert.Contains(
                        missingIdentifier,
                        screenText,
                        StringComparison.Ordinal);
                    await automator.ClickAtAsync(
                        25,
                        8,
                        MouseButton.Left,
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilAsync(
                        screen => screen.ContainsText(secondMissingIdentifier) &&
                            !screen.ContainsText(missingIdentifier),
                        description: "mouse click to focus the second diagnostic row")
                        .ConfigureAwait(false);
                    await automator.UpAsync(TestContext.CancellationToken)
                        .ConfigureAwait(false);
                    await automator.WaitUntilAsync(
                        screen => screen.ContainsText(missingIdentifier) &&
                            !screen.ContainsText(secondMissingIdentifier),
                        description: "keyboard navigation to focus the first diagnostic row")
                        .ConfigureAwait(false);
                    using (Hex1bTerminalSnapshot beforeColumnResizeSnapshot =
                        automator.CreateSnapshot())
                    {
                        string[] lines = beforeColumnResizeSnapshot.GetScreenText().Split('\n');
                        int headerRow = Array.FindIndex(
                            lines,
                            static line => line.Contains("Severity", StringComparison.Ordinal) &&
                                line.Contains("Code", StringComparison.Ordinal) &&
                                line.Contains("Location", StringComparison.Ordinal));
                        Assert.IsGreaterThanOrEqualTo(0, headerRow);
                        int initialCodeColumn = lines[headerRow].IndexOf(
                            "Code",
                            StringComparison.Ordinal);
                        Assert.IsGreaterThanOrEqualTo(0, initialCodeColumn);
                        int severityDividerColumn = lines[headerRow].LastIndexOf(
                            '│',
                            initialCodeColumn);
                        Assert.IsGreaterThanOrEqualTo(1, severityDividerColumn);
                        await automator.DragAsync(
                            severityDividerColumn - 1,
                            headerRow,
                            severityDividerColumn + 5,
                            headerRow,
                            MouseButton.Left,
                            TestContext.CancellationToken).ConfigureAwait(false);
                        await automator.WaitUntilAsync(
                            screen => screen
                                .GetScreenText()
                                .Split('\n')
                                .Any(line =>
                                    line.Contains("Severity", StringComparison.Ordinal) &&
                                    line.IndexOf("Code", StringComparison.Ordinal) >=
                                        initialCodeColumn + 5),
                            timeout: TimeSpan.FromSeconds(2),
                            description: "mouse drag to widen a real diagnostics table column")
                            .ConfigureAwait(false);
                    }

                    string yankText = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Error\tCS0103\tsrc/Features/Diagnostics/{longFileName}:1:19");
                    byte[] yankBytes = Encoding.UTF8.GetBytes(yankText);
                    byte[] clipboardSequence = Encoding.UTF8.GetBytes(
                        $"\u001b]52;c;{Convert.ToBase64String(yankBytes)}\a");
                    await automator.KeyAsync(
                        Hex1bKey.Y,
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilAsync(
                        _ => workload.ContainsRawOutput(clipboardSequence),
                        timeout: TimeSpan.FromSeconds(2),
                        description: "keyboard yank of the complete focused diagnostic row")
                        .ConfigureAwait(false);
                    await automator.WaitUntilAsync(
                        _ => workload.ContainsRawOutput("48;2;126;201;216m"u8),
                        timeout: TimeSpan.FromSeconds(2),
                        description: "visual yank flash on the focused diagnostic row")
                        .ConfigureAwait(false);

                    using Hex1bTerminalSnapshot beforeResizeSnapshot =
                        automator.CreateSnapshot();
                    string titleLine = beforeResizeSnapshot
                        .GetScreenText()
                        .Split('\n')
                        .Single(line =>
                            line.Contains("Views", StringComparison.Ordinal) &&
                            line.Contains("Diagnostics", StringComparison.Ordinal));
                    Assert.Contains("Diagnostics", titleLine, StringComparison.Ordinal);
                    await automator.DragAsync(
                        21,
                        12,
                        31,
                        12,
                        MouseButton.Left,
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilAsync(
                        screen => screen
                            .GetScreenText()
                            .Split('\n')
                            .Any(line =>
                                line.Contains("Diagnostics", StringComparison.Ordinal) &&
                                line.Length > 33 &&
                                line[33] == '┌'),
                        timeout: TimeSpan.FromSeconds(5),
                        description: "mouse drag to resize the dashboard columns")
                        .ConfigureAwait(false);
                    await automator.Ctrl().KeyAsync(
                        Hex1bKey.C,
                        TestContext.CancellationToken).ConfigureAwait(false);
                },
                TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(0, exitCode);
            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Selects a different live session with one mouse click.
    /// </summary>
    [TestMethod]
    public async Task DashboardSelectsSessionWithSingleMouseClick()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string workerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        string cliPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.App",
            "debug",
            "csls.dll");
        string cliWorkerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Cli.Worker",
            "debug",
            "csls-cli-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        Assert.IsTrue(File.Exists(cliPath), $"CLI launcher not found at {cliPath}.");
        Assert.IsTrue(File.Exists(cliWorkerPath), $"CLI worker not found at {cliWorkerPath}.");

        string firstFixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-dashboard-session-first-{Guid.NewGuid():N}");
        string secondFixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-dashboard-session-second-{Guid.NewGuid():N}");
        Directory.CreateDirectory(firstFixturePath);
        Directory.CreateDirectory(secondFixturePath);
        try
        {
            await File.WriteAllTextAsync(
                Path.Join(firstFixturePath, "First.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(secondFixturePath, "Second.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession firstLsp = await LspProcessSession.StartAsync(
                "csls-dashboard-first-session-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                firstFixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable firstLspCleanup =
                firstLsp.ConfigureAwait(false);
            await firstLsp.InitializeAsync(
                firstFixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await firstLsp.CompleteInitializationAsync().ConfigureAwait(false);
            await ControlSessionWaiter.WaitForRunningAsync(
                firstFixturePath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken,
                expectedProcessId: firstLsp.ProcessId).ConfigureAwait(false);

            LspProcessSession secondLsp = await LspProcessSession.StartAsync(
                "csls-dashboard-second-session-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                secondFixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable secondLspCleanup =
                secondLsp.ConfigureAwait(false);
            await secondLsp.InitializeAsync(
                secondFixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await secondLsp.CompleteInitializationAsync().ConfigureAwait(false);
            await ControlSessionWaiter.WaitForRunningAsync(
                secondFixturePath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken,
                expectedProcessId: secondLsp.ProcessId).ConfigureAwait(false);

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CSLS_CLI_WORKER_PATH"] = cliWorkerPath,
                ["DOTNET_HOST_PATH"] = EditorToolResolver.ResolveDotNetHost()
            };
            const int width = 120;
            const int height = 24;
            var workload = new Hex1bPtyWorkload(
                EditorToolResolver.ResolveDotNetHost(),
                [
                    cliPath,
                    "dashboard",
                    "--session",
                    firstLsp.ProcessId.ToString(CultureInfo.InvariantCulture)
                ],
                firstFixturePath,
                width,
                height,
                environment);
            await using ConfiguredAsyncDisposable workloadCleanup =
                workload.ConfigureAwait(false);
            using Hex1bTerminal terminal = Hex1bTerminal.CreateBuilder()
                .WithWorkload(workload)
                .WithHeadless()
                .WithDimensions(width, height)
                .Build();

            int exitCode = await workload.RunAsync(
                terminal,
                async () =>
                {
                    var automator = new Hex1bTerminalAutomator(
                        terminal,
                        defaultTimeout: TimeSpan.FromSeconds(60));
                    await automator.WaitUntilTextAsync("csls dashboard").ConfigureAwait(false);
                    await automator.WaitUntilTextAsync(
                        secondLsp.ProcessId.ToString(CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                    using Hex1bTerminalSnapshot sessionsSnapshot =
                        automator.CreateSnapshot();
                    string[] sessionLines = sessionsSnapshot
                        .GetScreenText()
                        .Split('\n');
                    int secondSessionRow = Array.FindIndex(
                        sessionLines,
                        line => line.Contains(
                            secondLsp.ProcessId.ToString(CultureInfo.InvariantCulture),
                            StringComparison.Ordinal));
                    Assert.AreNotEqual(
                        -1,
                        secondSessionRow,
                        sessionsSnapshot.GetScreenText());
                    await automator.ClickAtAsync(
                        25,
                        secondSessionRow,
                        MouseButton.Left,
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilAsync(
                        screen => screen.GetScreenText().StartsWith(
                            $"csls dashboard  session {secondLsp.ProcessId}",
                            StringComparison.Ordinal),
                        timeout: TimeSpan.FromSeconds(5),
                        description: "one mouse click to select the second live session")
                        .ConfigureAwait(false);
                    await automator.Ctrl().KeyAsync(
                        Hex1bKey.C,
                        TestContext.CancellationToken).ConfigureAwait(false);
                },
                TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(0, exitCode);
            string firstDiagnostics = await firstLsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            string secondDiagnostics = await secondLsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "Unhandled exception",
                firstDiagnostics,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Unhandled exception",
                secondDiagnostics,
                StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                firstFixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await DirectoryReleaseWaiter.DeleteAsync(
                secondFixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Renders the diagnostics view without waiting for real analyzer execution to finish.
    /// </summary>
    [TestMethod]
    public async Task DashboardDiagnosticsClickDoesNotWaitForAnalyzerExecution()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string workerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        string cliPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.App",
            "debug",
            "csls.dll");
        string cliWorkerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Cli.Worker",
            "debug",
            "csls-cli-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        Assert.IsTrue(File.Exists(cliPath), $"CLI launcher not found at {cliPath}.");
        Assert.IsTrue(File.Exists(cliWorkerPath), $"CLI worker not found at {cliWorkerPath}.");

        AnalyzerExecutionProbeFixture fixture = await AnalyzerExecutionProbeFixture.CreateAsync(
            repositoryRoot,
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        LspProcessSession lsp = await LspProcessSession.StartAsync(
            "csls-dashboard-diagnostics-latency-worker",
            EditorToolResolver.ResolveDotNetHost(),
            [workerPath],
            fixture.RootPath).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
        await lsp.InitializeAsync(
            fixture.RootPath,
            TestContext.CancellationToken).ConfigureAwait(false);
        await lsp.OpenDocumentAsync(
            fixture.DocumentPaths[0],
            fixture.DocumentTexts[0]).ConfigureAwait(false);
        await ControlSessionWaiter.WaitForRunningAsync(
            fixture.RootPath,
            TimeSpan.FromSeconds(60),
            TestContext.CancellationToken).ConfigureAwait(false);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CSLS_CLI_WORKER_PATH"] = cliWorkerPath,
            ["DOTNET_HOST_PATH"] = EditorToolResolver.ResolveDotNetHost()
        };
        const int width = 110;
        const int height = 28;
        var workload = new Hex1bPtyWorkload(
            EditorToolResolver.ResolveDotNetHost(),
            [
                cliPath,
                "dashboard",
                "--session",
                lsp.ProcessId.ToString(CultureInfo.InvariantCulture)
            ],
            fixture.RootPath,
            width,
            height,
            environment);
        await using ConfiguredAsyncDisposable workloadCleanup = workload.ConfigureAwait(false);
        using Hex1bTerminal terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithHeadless()
            .WithDimensions(width, height)
            .Build();

        int exitCode = await workload.RunAsync(
            terminal,
            async () =>
            {
                var automator = new Hex1bTerminalAutomator(
                    terminal,
                    defaultTimeout: TimeSpan.FromSeconds(60));
                try
                {
                    await automator.WaitUntilTextAsync("csls dashboard").ConfigureAwait(false);
                    await automator.ClickAtAsync(
                        4,
                        8,
                        MouseButton.Left,
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await FileTextWaiter.WaitAsync(
                        fixture.MarkerPath,
                        "started",
                        TimeSpan.FromSeconds(60),
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilAsync(
                        screen => screen.ContainsText("Loading diagnostics..."),
                        timeout: TimeSpan.FromSeconds(2),
                        description: "diagnostics view to render while real analyzer execution is blocked")
                        .ConfigureAwait(false);
                    await fixture.ReleaseAsync(TestContext.CancellationToken).ConfigureAwait(false);
                    await automator.WaitUntilAsync(
                        screen => screen.ContainsText("3 diagnostics") &&
                            screen.ContainsText("Analyzer execution probe"),
                        description: "completed real analyzer diagnostics")
                        .ConfigureAwait(false);
                    await automator.Ctrl().KeyAsync(
                        Hex1bKey.C,
                        TestContext.CancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await fixture.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
                }
            },
            TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(0, exitCode);
        string diagnostics = await lsp.ShutdownAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
    }

    private static async Task AssertFocusedRowYankAsync(
        Hex1bTerminalAutomator automator,
        Hex1bPtyWorkload workload,
        string expectedText,
        CancellationToken cancellationToken)
    {
        byte[] textBytes = Encoding.UTF8.GetBytes(expectedText);
        byte[] clipboardSequence = Encoding.UTF8.GetBytes(
            $"\u001b]52;c;{Convert.ToBase64String(textBytes)}\a");
        await automator.KeyAsync(Hex1bKey.Y, cancellationToken).ConfigureAwait(false);
        await automator.WaitUntilAsync(
            _ => workload.ContainsRawOutput(clipboardSequence),
            timeout: TimeSpan.FromSeconds(2),
            description: $"complete focused row yank for {expectedText}").ConfigureAwait(false);
    }

    private static int GetAcceptedRequestCount(string screenText)
    {
        foreach (string line in screenText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int labelIndex = line.IndexOf("Accepted:", StringComparison.Ordinal);
            if (labelIndex < 0)
            {
                continue;
            }

            ReadOnlySpan<char> value = line.AsSpan(labelIndex + "Accepted:".Length).Trim();
            int separatorIndex = value.IndexOf(' ');
            if (separatorIndex >= 0)
            {
                value = value[..separatorIndex];
            }

            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
            {
                return count;
            }
        }

        return -1;
    }
}
