using Csls.Protocol;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using System.Globalization;
using System.Runtime.CompilerServices;

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

            var lsp = LspProcessSession.Start(
                "csls-dashboard-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);
            await ControlSessionWaiter.WaitForRunningAsync(
                fixturePath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);

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
                    await automator.WaitUntilTextAsync("Initialized 1 workspace folders")
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
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    /// <summary>
    /// Cancels and traces a live Roslyn analyzer request through the Hex1b dashboard.
    /// </summary>
    [TestMethod]
    public async Task DashboardControlsLiveRequestTracing()
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
        CancellationProbeFixture fixture = await CancellationProbeFixture.CreateAsync(
            repositoryRoot,
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var lsp = LspProcessSession.Start(
            "csls-dashboard-cancellation-worker",
            EditorToolResolver.ResolveDotNetHost(),
            [workerPath],
            fixture.RootPath);
        await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
        await lsp.InitializeAsync(
            fixture.RootPath,
            TestContext.CancellationToken).ConfigureAwait(false);
        await lsp.OpenDocumentAsync(
            fixture.DocumentPath,
            CancellationProbeFixture.DocumentText).ConfigureAwait(false);
        await ControlSessionWaiter.WaitForRunningAsync(
            fixture.RootPath,
            TimeSpan.FromSeconds(60),
            TestContext.CancellationToken).ConfigureAwait(false);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CSLS_CLI_WORKER_PATH"] = cliWorkerPath,
            ["DOTNET_HOST_PATH"] = EditorToolResolver.ResolveDotNetHost()
        };
        const int width = 160;
        const int height = 40;
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
                await automator.KeyAsync(
                    Hex1bKey.F11,
                    TestContext.CancellationToken).ConfigureAwait(false);
                await automator.WaitUntilTextAsync("Start request tracing?").ConfigureAwait(false);
                await automator.EnterAsync(TestContext.CancellationToken).ConfigureAwait(false);
                await automator.WaitUntilTextAsync("is active.").ConfigureAwait(false);

                Task<DocumentDiagnosticReport> diagnosticRequest = lsp.RequestDiagnosticsAsync(
                    fixture.DocumentPath,
                    previousResultId: null,
                    TestContext.CancellationToken);
                await FileTextWaiter.WaitAsync(
                    fixture.MarkerPath,
                    "started",
                    TimeSpan.FromSeconds(60),
                    TestContext.CancellationToken).ConfigureAwait(false);
                await automator.KeyAsync(
                    Hex1bKey.F5,
                    TestContext.CancellationToken).ConfigureAwait(false);
                await automator.WaitUntilTextAsync("Refreshed live session state.")
                    .ConfigureAwait(false);
                await automator.KeyAsync(
                    Hex1bKey.F2,
                    TestContext.CancellationToken).ConfigureAwait(false);
                await automator.WaitUntilTextAsync("textDocument/diagnostic")
                    .ConfigureAwait(false);
                await automator.KeyAsync(
                    Hex1bKey.F10,
                    TestContext.CancellationToken).ConfigureAwait(false);
                await automator.WaitUntilTextAsync("Cancel textDocument/diagnostic?")
                    .ConfigureAwait(false);
                await automator.EnterAsync(TestContext.CancellationToken).ConfigureAwait(false);
                await automator.WaitUntilTextAsync("Cancellation requested for")
                    .ConfigureAwait(false);
                await FileTextWaiter.WaitAsync(
                    fixture.MarkerPath,
                    "canceled",
                    TimeSpan.FromSeconds(60),
                    TestContext.CancellationToken).ConfigureAwait(false);
                TaskCanceledException? canceledRequest = null;
                try
                {
                    await diagnosticRequest.ConfigureAwait(false);
                }
                catch (TaskCanceledException exception)
                {
                    canceledRequest = exception;
                }

                Assert.IsNotNull(canceledRequest);
                Assert.IsFalse(TestContext.CancellationToken.IsCancellationRequested);

                await automator.KeyAsync(
                    Hex1bKey.F11,
                    TestContext.CancellationToken).ConfigureAwait(false);
                await automator.WaitUntilTextAsync("Stop request tracing?").ConfigureAwait(false);
                await automator.EnterAsync(TestContext.CancellationToken).ConfigureAwait(false);
                await automator.WaitUntilTextAsync("is stopped.").ConfigureAwait(false);
                await automator.KeyAsync(
                    Hex1bKey.F3,
                    TestContext.CancellationToken).ConfigureAwait(false);
                await automator.WaitUntilTextAsync("Trace: stopped").ConfigureAwait(false);
                await automator.WaitUntilTextAsync("textDocument/diagnostic")
                    .ConfigureAwait(false);
                await automator.WaitUntilTextAsync("Canceled").ConfigureAwait(false);
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
