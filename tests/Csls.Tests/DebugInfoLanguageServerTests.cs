using Csls.Protocol;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Verifies live debug observations through a real language-server process.
/// </summary>
[TestClass]
public sealed class DebugInfoLanguageServerTests
{
    private static readonly TimeSpan s_phaseTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Reports real workspace phases, folders, scheduler counters, and timings.
    /// </summary>
    [TestMethod]
    public async Task DebugInfoReportsLiveSessionState()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = ResolveWorkerPath(repositoryRoot);
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-debug-info-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string projectPath = Path.Join(fixturePath, "DebugInfoFixture.csproj");
            string documentPath = Path.Join(fixturePath, "Program.cs");
            await File.WriteAllTextAsync(
                projectPath,
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-debug-info-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);

            CSharpDebugInfo uninitialized = await lsp.RequestDebugInfoAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("Uninitialized", uninitialized.Workspace.Phase);
            Assert.IsEmpty(uninitialized.Workspace.Folders);
            Assert.IsEmpty(uninitialized.RequestQueue.Requests);

            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            CSharpDebugInfo configured = await lsp.RequestDebugInfoAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("Configured", configured.Workspace.Phase);
            Assert.AreEqual(0L, configured.Workspace.Generation);
            Assert.IsEmpty(configured.Workspace.Folders);
            Assert.IsEmpty(configured.RequestQueue.Stats);

            await lsp.CompleteInitializationAsync().ConfigureAwait(false);
            CSharpDebugInfo ready = await WaitForPhaseAsync(
                lsp,
                "Ready",
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(1L, ready.Workspace.Generation);
            CSharpDebugWorkspaceFolderInfo folder = Assert.ContainsSingle(
                ready.Workspace.Folders);
            Assert.AreEqual(DocumentUri.FromFileSystemPath(fixturePath), folder.Uri);
            Assert.AreEqual(Path.GetFileName(fixturePath), folder.Name);
            Assert.AreEqual("MSBuildWorkspace", folder.WorkspaceKind);
            Assert.AreEqual(1, folder.ProjectCount);
            Assert.IsGreaterThanOrEqualTo(1, folder.DocumentCount);
            CSharpDebugRequestStatisticsInfo initialized = Assert.ContainsSingle(
                ready.RequestQueue.Stats.Where(
                    static statistic => statistic.Name == "initialized"));
            Assert.AreEqual(1L, initialized.Count);
            Assert.IsGreaterThanOrEqualTo(0, initialized.AverageDurationMs);
            Assert.IsGreaterThanOrEqualTo(
                initialized.MaxDurationMs,
                initialized.AverageDurationMs);
            Assert.DoesNotContain(
                "$/csharp/debugInfo",
                ready.RequestQueue.Requests.Select(static request => request.Name));

            await lsp.RequestShutdownAsync(TestContext.CancellationToken).ConfigureAwait(false);
            CSharpDebugInfo shuttingDown = await lsp.RequestDebugInfoAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("ShuttingDown", shuttingDown.Workspace.Phase);
            string diagnostics = await lsp.ExitAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Acknowledges shutdown while the real repository workspace is still loading.
    /// </summary>
    [TestMethod]
    public async Task ShutdownRespondsWhileRepositoryWorkspaceLoads()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = ResolveWorkerPath(repositoryRoot);
        LspProcessSession lsp = await LspProcessSession.StartAsync(
            "csls-shutdown-during-load-worker",
            EditorToolResolver.ResolveDotNetHost(),
            [workerPath],
            repositoryRoot).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);

        await lsp.InitializeAsync(
            repositoryRoot,
            TestContext.CancellationToken).ConfigureAwait(false);
        await lsp.CompleteInitializationAsync().ConfigureAwait(false);

        using var loadingTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        loadingTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        CSharpDebugInfo loading = await WaitForPhaseAsync(
            lsp,
            "Loading",
            loadingTimeout.Token).ConfigureAwait(false);
        Assert.AreEqual(0L, loading.Workspace.Generation);
        Assert.IsEmpty(loading.Workspace.Folders);
        CSharpDebugRequestInfo initialized = Assert.ContainsSingle(
            loading.RequestQueue.Requests.Where(
                static request => request.Name == "initialized"));
        Assert.AreEqual("Running", initialized.Phase);

        using var shutdownTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        shutdownTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        await lsp.RequestShutdownAsync(shutdownTimeout.Token).ConfigureAwait(false);
        CSharpDebugInfo shuttingDown = await lsp.RequestDebugInfoAsync(
            shutdownTimeout.Token).ConfigureAwait(false);
        Assert.AreEqual("ShuttingDown", shuttingDown.Workspace.Phase);
        string diagnostics = await lsp.ExitAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
    }

    /// <summary>
    /// Responds while a real Roslyn analyzer holds a scheduled diagnostic request.
    /// </summary>
    [TestMethod]
    public async Task DebugInfoBypassesBlockedRequestScheduling()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = ResolveWorkerPath(repositoryRoot);
        AnalyzerExecutionProbeFixture fixture = await AnalyzerExecutionProbeFixture.CreateAsync(
            repositoryRoot,
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        LspProcessSession lsp = await LspProcessSession.StartAsync(
            "csls-debug-info-blocked-worker",
            EditorToolResolver.ResolveDotNetHost(),
            [workerPath],
            fixture.RootPath).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
        bool analyzerReleased = false;
        try
        {
            await lsp.InitializeAsync(
                fixture.RootPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(
                fixture.DocumentPaths[0],
                fixture.DocumentTexts[0]).ConfigureAwait(false);
            Task<DocumentDiagnosticReport> diagnosticTask = lsp.RequestDiagnosticsAsync(
                fixture.DocumentPaths[0],
                previousResultId: null,
                TestContext.CancellationToken);
            await FileTextWaiter.WaitAsync(
                fixture.MarkerPath,
                "started",
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);

            using var debugTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            debugTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            CSharpDebugInfo blocked = await lsp.RequestDebugInfoAsync(
                debugTimeout.Token).ConfigureAwait(false);
            CSharpDebugRequestInfo activeDiagnostic = Assert.ContainsSingle(
                blocked.RequestQueue.Requests.Where(
                    static request => request.Name == "textDocument/diagnostic"));
            Assert.AreEqual("Running", activeDiagnostic.Phase);
            Assert.IsGreaterThanOrEqualTo(0, activeDiagnostic.DurationMs);

            await fixture.ReleaseAsync(TestContext.CancellationToken).ConfigureAwait(false);
            analyzerReleased = true;
            DocumentDiagnosticReport diagnostic = await diagnosticTask.ConfigureAwait(false);
            Assert.AreEqual("full", diagnostic.Kind);
            CSharpDebugInfo completed = await lsp.RequestDebugInfoAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            CSharpDebugRequestStatisticsInfo statistic = Assert.ContainsSingle(
                completed.RequestQueue.Stats.Where(
                    static item => item.Name == "textDocument/diagnostic"));
            Assert.AreEqual(1L, statistic.Count);
            Assert.IsGreaterThan(0, statistic.AverageDurationMs);
            Assert.IsGreaterThanOrEqualTo(
                statistic.MaxDurationMs,
                statistic.AverageDurationMs);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            if (!analyzerReleased)
            {
                await fixture.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task<CSharpDebugInfo> WaitForPhaseAsync(
        LspProcessSession lsp,
        string expectedPhase,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(s_phaseTimeout);
        using var timer = new PeriodicTimer(s_pollInterval);
        while (true)
        {
            CSharpDebugInfo info = await lsp.RequestDebugInfoAsync(
                timeoutSource.Token).ConfigureAwait(false);
            if (string.Equals(
                info.Workspace.Phase,
                expectedPhase,
                StringComparison.Ordinal))
            {
                return info;
            }

            if (!await timer.WaitForNextTickAsync(timeoutSource.Token).ConfigureAwait(false))
            {
                throw new UnreachableException();
            }
        }
    }

    private static string ResolveWorkerPath(string repositoryRoot)
    {
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        return workerPath;
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
        namespace DebugInfoFixture;

        public static class Program
        {
            public static void Main()
            {
            }
        }
        """;
}
