using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

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
    /// Loads the complete repository and reports project diagnostics without workspace failures.
    /// </summary>
    [TestMethod]
    public async Task RepositoryWorkspaceLoadsAndReportsProjectDiagnostics()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        LspProcessSession lsp = await LspProcessSession.StartAsync(
            "csls-repository-diagnostic-worker",
            EditorToolResolver.ResolveDotNetHost(),
            [workerPath],
            repositoryRoot).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
        using var capabilities = JsonDocument.Parse(
            """
            {
              "workspace": {
                "workspaceEdit": {
                  "documentChanges": true,
                  "resourceOperations": ["create"]
                }
              },
              "textDocument": {
                "diagnostic": {}
              }
            }
            """);
        await lsp.InitializeAsync(
            repositoryRoot,
            capabilities.RootElement,
            TestContext.CancellationToken).ConfigureAwait(false);
        await lsp.CompleteInitializationAsync().ConfigureAwait(false);
        ControlSessionInfo session;
        try
        {
            session = await ControlSessionWaiter.WaitForRunningAsync(
                repositoryRoot,
                TimeSpan.FromMinutes(3),
                TestContext.CancellationToken,
                expectedProcessId: lsp.ProcessId).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await WriteStartupFailureDiagnosticsAsync(lsp).ConfigureAwait(false);
            throw;
        }
        var control = new ControlRpcClient(session.SocketPath);
        await using ConfiguredAsyncDisposable controlCleanup = control.ConfigureAwait(false);
        string requestContextPath = Path.Join(
            repositoryRoot,
            "src",
            "Csls.Core",
            "RequestContext.cs");
        string requestContextText = await File.ReadAllTextAsync(
            requestContextPath,
            TestContext.CancellationToken).ConfigureAwait(false);
        await lsp.OpenDocumentAsync(requestContextPath, requestContextText)
            .ConfigureAwait(false);
        IReadOnlyList<CodeAction> actions = await lsp.RequestCodeActionsAsync(
            requestContextPath,
            new LspRange(new Position(6, 0), new Position(6, 0)),
            ["refactor"],
            TestContext.CancellationToken).ConfigureAwait(false);
        string[] actionTitles = [.. actions.Select(static action => action.Title)];
        string actualActions = $"Actual actions: {string.Join(", ", actionTitles)}";
        Assert.Contains("Extract base class...", actionTitles, actualActions);
        Assert.Contains("Convert to positional record", actionTitles, actualActions);
        Assert.Contains("Add 'DebuggerDisplay' attribute", actionTitles, actualActions);
        Assert.IsTrue(
            actions
                .Where(static action =>
                    action.Title is "Extract base class..." or
                        "Convert to positional record" or
                        "Add 'DebuggerDisplay' attribute")
                .All(static action => action.Edit is { DocumentChanges.Count: > 0 }),
            actualActions);
        string requestActivityStatePath = Path.Join(
            repositoryRoot,
            "src",
            "Csls.Core",
            "RequestActivityState.cs");
        string requestActivityStateText = await File.ReadAllTextAsync(
            requestActivityStatePath,
            TestContext.CancellationToken).ConfigureAwait(false);
        await lsp.OpenDocumentAsync(
            requestActivityStatePath,
            requestActivityStateText).ConfigureAwait(false);
        string[] requestActivityStateLines = requestActivityStateText.Split('\n');
        int conditionalAccessLine = Array.FindIndex(
            requestActivityStateLines,
            static line => line.Contains(
                "_traceRecord?.MarkCancellationRequested()",
                StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(0, conditionalAccessLine);
        int conditionalAccessCharacter = requestActivityStateLines[conditionalAccessLine]
            .IndexOf("MarkCancellationRequested", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, conditionalAccessCharacter);
        Position conditionalAccessPosition = new(
            conditionalAccessLine,
            conditionalAccessCharacter);
        IReadOnlyList<CodeAction> conditionalAccessActions =
            await lsp.RequestCodeActionsAsync(
                requestActivityStatePath,
                new LspRange(conditionalAccessPosition, conditionalAccessPosition),
                ["refactor"],
                TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsEmpty(
            conditionalAccessActions.Where(static action =>
                action.Title.StartsWith("Introduce ", StringComparison.Ordinal)),
            string.Join(
                Environment.NewLine,
                conditionalAccessActions.Select(static action => action.Title)));

        ControlDashboardSnapshot snapshot = await control.GetDashboardSnapshotAsync(
            new ControlDashboardRequest { IncludeDiagnostics = false },
            TestContext.CancellationToken).ConfigureAwait(false);
        ControlProjectInfo coreProject = snapshot.Projects.Single(static project =>
            project.Name == "Csls.Core");
        ControlDashboardSnapshot diagnosticSnapshot = await control.GetDashboardSnapshotAsync(
            new ControlDashboardRequest
            {
                IncludeDiagnostics = true,
                DiagnosticsProjectId = coreProject.Id
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            0,
            diagnosticSnapshot.TotalDiagnostics,
            string.Join(
                Environment.NewLine,
                diagnosticSnapshot.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Severity} {diagnostic.Id} " +
                    $"{diagnostic.FilePath}:{diagnostic.Line + 1}:" +
                    $"{diagnostic.Character + 1}: {diagnostic.Message}")));
        Assert.Contains(
            "Generate-Docs.cs",
            snapshot.Projects.Select(static project => project.Name));
        ControlWorkspaceOperationResult reload = await control.ReloadWorkspaceAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(reload.PreviousGeneration + 1, reload.CurrentGeneration);
        Assert.AreEqual(1, reload.AffectedWorkspaceCount);
        ControlDashboardSnapshot reloadedSnapshot = await control.GetDashboardSnapshotAsync(
            new ControlDashboardRequest { IncludeDiagnostics = false },
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.HasCount(snapshot.Projects.Count, reloadedSnapshot.Projects);
        Assert.Contains(
            "Generate-Docs.cs",
            reloadedSnapshot.Projects.Select(static project => project.Name));
        string standardError = await lsp.ShutdownAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        TestContext.WriteLine(standardError);

        Assert.DoesNotContain("warn:", standardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fail:", standardError, StringComparison.OrdinalIgnoreCase);
    }

    private async Task WriteStartupFailureDiagnosticsAsync(LspProcessSession lsp)
    {
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        TestContext.WriteLine(await lsp.ShutdownAsync(cleanupTimeout.Token).ConfigureAwait(false));
    }
}
