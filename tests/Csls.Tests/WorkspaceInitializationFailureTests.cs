using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using StreamJsonRpc;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies initialization failures remain observable through real server transports.
/// </summary>
[TestClass]
public sealed class WorkspaceInitializationFailureTests
{
    /// <summary>
    /// Gets the framework-managed test cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Reports malformed solution failures through control logs and standard-error diagnostics.
    /// </summary>
    /// <param name="workDoneProgress">Whether the client supports workspace progress.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task InvalidSolutionReportsInitializationFailure(bool workDoneProgress)
    {
        string fixturePath = Path.Join(Path.GetTempPath(), $"csls-init-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            await File.WriteAllTextAsync(Path.Join(fixturePath, "App.cs"), "public sealed class App;",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Invalid.slnx"),
                "<Solution><Project Path=\"App.csproj\"></Solution>",
                TestContext.CancellationToken).ConfigureAwait(false);
            string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
            string workerPath = Path.Join(
                EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
                "bin", "Csls.Worker", "debug", "csls-worker.dll");
            string socketDirectory = Path.Join(fixturePath, "sockets");
            var client = new LspTestClient(legacyConfiguration: null, preferredConfiguration: null);
            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-initialization-failure",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath,
                client,
                new Dictionary<string, string>
                {
                    ["CSLS_CONTROL_SOCKET_DIRECTORY"] = socketDirectory
                }).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = lsp.ConfigureAwait(false);
            using var capabilities = JsonDocument.Parse(workDoneProgress
                ? "{\"window\":{\"workDoneProgress\":true}}"
                : "{}");
            await lsp.InitializeAsync(fixturePath, capabilities.RootElement, TestContext.CancellationToken)
                .ConfigureAwait(false);
            var control = new ControlRpcClient(Path.Join(socketDirectory, $"{lsp.ProcessId}.csls.socket"));
            await using ConfiguredAsyncDisposable controlCleanup = control.ConfigureAwait(false);
            ControlDashboardSnapshot configured = await control.GetDashboardSnapshotAsync(
                new ControlDashboardRequest(), TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("Configured", configured.Session.WorkspacePhase);
            Assert.IsEmpty(configured.Projects);
            await lsp.CompleteInitializationAsync().ConfigureAwait(false);

            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => ControlSessionWaiter.WaitForRunningAsync(
                    fixturePath,
                    TimeSpan.FromSeconds(30),
                    TestContext.CancellationToken,
                    expectedProcessId: lsp.ProcessId,
                    socketDirectory: socketDirectory)).ConfigureAwait(false);
            Assert.Contains("Workspace initialization failed", failure.Message, StringComparison.Ordinal);
            Assert.Contains("does not match the end tag", failure.Message, StringComparison.Ordinal);
            RemoteInvocationException definitionFailure = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
                () => lsp.RequestDefinitionsAsync(Path.Join(fixturePath, "App.cs"),
                    new Position(0, 0), TestContext.CancellationToken)).ConfigureAwait(false);
            Assert.Contains("does not match the end tag", definitionFailure.Message, StringComparison.Ordinal);
            string diagnostics = await lsp.ShutdownAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("Workspace initialization failed", diagnostics, StringComparison.Ordinal);
            Assert.Contains("System.Xml.XmlException", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
    }
}
