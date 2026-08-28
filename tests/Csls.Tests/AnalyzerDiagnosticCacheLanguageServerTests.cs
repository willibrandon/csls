using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using StreamJsonRpc;
using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Verifies analyzer diagnostic sharing through real LSP, Roslyn, and control processes.
/// </summary>
[TestClass]
public sealed class AnalyzerDiagnosticCacheLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Shares one project analysis across callers while isolating caller cancellation.
    /// </summary>
    [TestMethod]
    public async Task ConcurrentPullDiagnosticsShareProjectAnalysis()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        AnalyzerExecutionProbeFixture fixture = await AnalyzerExecutionProbeFixture.CreateAsync(
            repositoryRoot,
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var lsp = LspProcessSession.Start(
            "csls-analyzer-cache-worker",
            EditorToolResolver.ResolveDotNetHost(),
            [workerPath],
            fixture.RootPath,
            environmentVariables: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DOTNET_PROCESSOR_COUNT"] = "4"
            });
        await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
        await lsp.InitializeAsync(
            fixture.RootPath,
            TestContext.CancellationToken).ConfigureAwait(false);
        for (int index = 0; index < fixture.DocumentPaths.Count; index++)
        {
            await lsp.OpenDocumentAsync(
                fixture.DocumentPaths[index],
                fixture.DocumentTexts[index]).ConfigureAwait(false);
        }

        var controlClient = new ControlRpcClient(ControlEndpoint.GetSocketPath(lsp.ProcessId));
        await using ConfiguredAsyncDisposable controlCleanup =
            controlClient.ConfigureAwait(false);
        bool analyzerReleased = false;
        try
        {
            Task<DocumentDiagnosticReport> owner = lsp.RequestDiagnosticsAsync(
                fixture.DocumentPaths[0],
                previousResultId: null,
                TestContext.CancellationToken);
            await FileTextWaiter.WaitAsync(
                fixture.MarkerPath,
                "started",
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);
            Task<DocumentDiagnosticReport> follower = lsp.RequestDiagnosticsAsync(
                fixture.DocumentPaths[1],
                previousResultId: null,
                TestContext.CancellationToken);
            Task<DocumentDiagnosticReport> observer = lsp.RequestDiagnosticsAsync(
                fixture.DocumentPaths[2],
                previousResultId: null,
                TestContext.CancellationToken);
            IReadOnlyList<ControlRequestInfo> activeRequests =
                await ControlRequestWaiter.WaitForActiveCountAsync(
                    controlClient,
                    "textDocument/diagnostic",
                    expectedCount: 3,
                    TimeSpan.FromSeconds(60),
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(3, activeRequests);

            ControlRequestInfo ownerRequest = activeRequests.MinBy(
                static request => request.Ordinal)
                ?? throw new InvalidDataException("No owner diagnostic request was active.");
            ControlCancelRequestResult cancellation = await controlClient.CancelRequestAsync(
                new ControlCancelRequest { CorrelationId = ownerRequest.CorrelationId },
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(cancellation.CancellationRequested);
            TaskCanceledException? ownerCancellationException = null;
            try
            {
                await owner.ConfigureAwait(false);
            }
            catch (TaskCanceledException exception)
            {
                ownerCancellationException = exception;
            }

            Assert.IsNotNull(ownerCancellationException);
            Assert.IsFalse(TestContext.CancellationToken.IsCancellationRequested);
            await fixture.ReleaseAsync(TestContext.CancellationToken).ConfigureAwait(false);
            analyzerReleased = true;
            DocumentDiagnosticReport[] sharedReports = await Task.WhenAll(
                follower,
                observer).ConfigureAwait(false);
            foreach (DocumentDiagnosticReport report in sharedReports)
            {
                AssertDiagnosticReport(report);
            }

            DocumentDiagnosticReport cached = await lsp.RequestDiagnosticsAsync(
                fixture.DocumentPaths[0],
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            AssertDiagnosticReport(cached);
            string[] firstGenerationEvents = await fixture.ReadEventsAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(2, firstGenerationEvents);
            Assert.AreEqual("started", firstGenerationEvents[0]);
            Assert.AreEqual("released", firstGenerationEvents[1]);

            fixture.ResetRelease();
            analyzerReleased = false;
            await lsp.ChangeDocumentAsync(
                fixture.DocumentPaths[0],
                version: 2,
                [
                    new TextDocumentContentChangeEvent
                    {
                        Text = AnalyzerExecutionProbeFixture.UpdatedFirstDocumentText
                    }
                ]).ConfigureAwait(false);
            Task<DocumentDiagnosticReport> updatedTask = lsp.RequestDiagnosticsAsync(
                fixture.DocumentPaths[0],
                cached.ResultId,
                TestContext.CancellationToken);
            await FileTextWaiter.WaitAsync(
                fixture.MarkerPath,
                $"released{Environment.NewLine}started",
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);
            await fixture.ReleaseAsync(TestContext.CancellationToken).ConfigureAwait(false);
            analyzerReleased = true;
            DocumentDiagnosticReport updated = await updatedTask.ConfigureAwait(false);
            AssertDiagnosticReport(updated);
            Assert.AreNotEqual(cached.ResultId, updated.ResultId);
            string[] allEvents = await fixture.ReadEventsAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(4, allEvents);
            Assert.AreEqual("started", allEvents[2]);
            Assert.AreEqual("released", allEvents[3]);

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

    /// <summary>
    /// Returns the LSP server-cancelled error when a diagnostic snapshot becomes stale.
    /// </summary>
    [TestMethod]
    public async Task StalePullDiagnosticReturnsServerCancelledError()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        AnalyzerExecutionProbeFixture fixture = await AnalyzerExecutionProbeFixture.CreateAsync(
            repositoryRoot,
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var lsp = LspProcessSession.Start(
            "csls-stale-diagnostic-worker",
            EditorToolResolver.ResolveDotNetHost(),
            [workerPath],
            fixture.RootPath);
        await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
        await lsp.InitializeAsync(
            fixture.RootPath,
            TestContext.CancellationToken).ConfigureAwait(false);
        await lsp.OpenDocumentAsync(
            fixture.DocumentPaths[0],
            fixture.DocumentTexts[0]).ConfigureAwait(false);

        var controlClient = new ControlRpcClient(ControlEndpoint.GetSocketPath(lsp.ProcessId));
        await using ConfiguredAsyncDisposable controlCleanup =
            controlClient.ConfigureAwait(false);
        ControlSessionInfo initialSession = await controlClient.GetSessionAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        bool analyzerReleased = false;
        try
        {
            Task<DocumentDiagnosticReport> diagnosticRequest = lsp.RequestDiagnosticsAsync(
                fixture.DocumentPaths[0],
                previousResultId: null,
                TestContext.CancellationToken);
            await FileTextWaiter.WaitAsync(
                fixture.MarkerPath,
                "started",
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.ChangeDocumentAsync(
                fixture.DocumentPaths[0],
                version: 2,
                [
                    new TextDocumentContentChangeEvent
                    {
                        Text = AnalyzerExecutionProbeFixture.UpdatedFirstDocumentText
                    }
                ]).ConfigureAwait(false);
            await WaitForGenerationAsync(
                controlClient,
                initialSession.WorkspaceGeneration + 1,
                TestContext.CancellationToken).ConfigureAwait(false);
            await fixture.ReleaseAsync(TestContext.CancellationToken).ConfigureAwait(false);
            analyzerReleased = true;

            RemoteInvocationException? cancellation = null;
            try
            {
                await diagnosticRequest.ConfigureAwait(false);
            }
            catch (RemoteInvocationException exception)
            {
                cancellation = exception;
            }

            Assert.IsNotNull(cancellation);
            Assert.AreEqual(typeof(RemoteInvocationException), cancellation.GetType());
            Assert.AreEqual(LspServerCancelledException.ErrorCode, cancellation.ErrorCode);
            Assert.Contains(
                "workspace changed",
                cancellation.Message,
                StringComparison.OrdinalIgnoreCase);

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

    private static void AssertDiagnosticReport(DocumentDiagnosticReport report)
    {
        Assert.AreEqual("full", report.Kind);
        Assert.IsNotNull(report.ResultId);
        IReadOnlyList<Diagnostic> diagnostics = report.Items
            ?? throw new InvalidDataException("A full diagnostic report had no items.");
        Assert.Contains(
            "CSLSTEST002",
            diagnostics.Select(static diagnostic => diagnostic.Code));
    }

    private static async Task WaitForGenerationAsync(
        ControlRpcClient client,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            ControlSessionInfo session = await client.GetSessionAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            if (session.WorkspaceGeneration >= expectedGeneration)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeoutSource.Token)
                .ConfigureAwait(false);
        }
    }
}
