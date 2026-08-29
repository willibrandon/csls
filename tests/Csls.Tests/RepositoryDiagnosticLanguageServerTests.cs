using Csls.Control;
using Csls.Control.Contracts;
using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Verifies the repository workspace through a real language-server process and control socket.
/// </summary>
[TestClass]
public sealed class RepositoryDiagnosticLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Loads the complete repository without compiler, analyzer, or workspace failures.
    /// </summary>
    [TestMethod]
    public async Task RepositoryWorkspaceLoadsWithoutDiagnosticsOrWorkspaceFailures()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        var lsp = LspProcessSession.Start(
            "csls-repository-diagnostic-worker",
            EditorToolResolver.ResolveDotNetHost(),
            [workerPath],
            repositoryRoot);
        await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
        await lsp.InitializeAsync(repositoryRoot, TestContext.CancellationToken)
            .ConfigureAwait(false);
        await lsp.CompleteInitializationAsync().ConfigureAwait(false);
        ControlSessionInfo session = await ControlSessionWaiter.WaitForRunningAsync(
            repositoryRoot,
            TimeSpan.FromSeconds(60),
            TestContext.CancellationToken).ConfigureAwait(false);
        var control = new ControlRpcClient(session.SocketPath);
        await using ConfiguredAsyncDisposable controlCleanup = control.ConfigureAwait(false);
        ControlDashboardSnapshot snapshot = await control.GetDashboardSnapshotAsync(
            new ControlDashboardRequest { IncludeDiagnostics = true },
            TestContext.CancellationToken).ConfigureAwait(false);
        string standardError = await lsp.ShutdownAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);

        Assert.AreEqual(
            0,
            snapshot.TotalDiagnostics,
            string.Join(
                Environment.NewLine,
                snapshot.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Severity} {diagnostic.Id} " +
                    $"{diagnostic.FilePath}:{diagnostic.Line + 1}:" +
                    $"{diagnostic.Character + 1}: {diagnostic.Message}")));
        Assert.DoesNotContain("warn:", standardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fail:", standardError, StringComparison.OrdinalIgnoreCase);
    }
}
