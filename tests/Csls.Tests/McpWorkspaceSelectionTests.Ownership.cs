using Csls.Control;
using Csls.Control.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies workspace ownership, exit isolation, and concurrent acquisition through real MCP processes.
/// </summary>
public sealed partial class McpWorkspaceSelectionTests
{
    /// <summary>
    /// Reuses a nested live workspace and stops only the transient target when MCP disconnects.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task NestedLiveWorkspaceReuseAndDisconnectPreserveAttachedSession()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        (string serverWorkerPath, string mcpPath, string mcpWorkerPath) =
            ResolveProductPaths(repositoryRoot);
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-mcp-live-reuse-{Guid.NewGuid():N}");
        string ownedFixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-mcp-owned-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        Directory.CreateDirectory(ownedFixturePath);
        try
        {
            string projectPath = Path.Join(fixturePath, "Attached.csproj");
            string documentPath = Path.Join(fixturePath, "Nested", "Attached.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(documentPath)!);
            await WriteWorkspaceAsync(
                projectPath,
                documentPath,
                "namespace Attached; public static class Marker { public const int Value = 42; }",
                TestContext.CancellationToken).ConfigureAwait(false);
            string ownedProjectPath = Path.Join(ownedFixturePath, "Owned.csproj");
            string ownedDocumentPath = Path.Join(ownedFixturePath, "Owned.cs");
            await WriteWorkspaceAsync(
                ownedProjectPath,
                ownedDocumentPath,
                "namespace Owned; public static class Marker { public const int Value = 7; }",
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession attached = await StartLanguageServerAsync(
                repositoryRoot,
                serverWorkerPath,
                fixturePath,
                "csls-mcp-attached-reuse",
                TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable attachedCleanup = attached.ConfigureAwait(false);
            McpProcessSession mcp = await McpProcessSession.StartAsync(
                repositoryRoot,
                mcpPath,
                mcpWorkerPath,
                serverWorkerPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable mcpCleanup = mcp.ConfigureAwait(false);
            McpClient client = mcp.Client;
            int ownedProcessId = 0;
            ProcessExitObservation? ownedExit = null;
            try
            {
                ControlSessionInfo selectedByDocument = await CallSessionAsync(
                    client,
                    new Dictionary<string, object?> { ["workspace"] = documentPath },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(attached.ProcessId, selectedByDocument.ProcessId);
                Assert.AreEqual(fixturePath, Assert.ContainsSingle(selectedByDocument.WorkspaceRoots));

                ControlSessionInfo selectedByProject = await CallSessionAsync(
                    client,
                    new Dictionary<string, object?> { ["workspace"] = projectPath },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(attached.ProcessId, selectedByProject.ProcessId);
                Assert.AreEqual(
                    selectedByDocument.SocketPath,
                    selectedByProject.SocketPath);

                ControlSessionInfo owned = await CallSessionAsync(
                    client,
                    new Dictionary<string, object?> { ["workspace"] = ownedProjectPath },
                    TestContext.CancellationToken).ConfigureAwait(false);
                ownedProcessId = owned.ProcessId;
                ownedExit = ProcessExitWaiter.Observe(ownedProcessId);
                Assert.AreNotEqual(attached.ProcessId, ownedProcessId);
                Assert.AreEqual(ownedProjectPath, Assert.ContainsSingle(owned.WorkspaceRoots));
            }
            finally
            {
                await DisconnectMcpAsync(
                    mcp,
                    TestContext.CancellationToken).ConfigureAwait(false);
            }

            await ProcessExitWaiter.WaitAsync(
                ownedExit ?? throw new InvalidOperationException(
                    "The MCP server did not create the owned target."),
                TimeSpan.FromSeconds(10),
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertFileDeletedAsync(
                ControlEndpoint.GetSocketPath(ownedProcessId),
                TimeSpan.FromSeconds(10),
                TestContext.CancellationToken).ConfigureAwait(false);

            var attachedControlClient = new ControlRpcClient(
                ControlEndpoint.GetSocketPath(attached.ProcessId));
            await using ConfiguredAsyncDisposable attachedControlCleanup =
                attachedControlClient.ConfigureAwait(false);
            ControlSessionInfo survivingAttached = await attachedControlClient.GetSessionAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(attached.ProcessId, survivingAttached.ProcessId);
            string diagnostics = await attached.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await DirectoryReleaseWaiter.DeleteAsync(
                ownedFixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Isolates an exited attached target and permits a later workspace call to resolve a replacement.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ExitedTargetIsolatedAndWorkspaceCanResolveAgain()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        (string serverWorkerPath, string mcpPath, string mcpWorkerPath) =
            ResolveProductPaths(repositoryRoot);
        string firstFixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-mcp-exit-first-{Guid.NewGuid():N}");
        string secondFixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-mcp-exit-second-{Guid.NewGuid():N}");
        Directory.CreateDirectory(firstFixturePath);
        Directory.CreateDirectory(secondFixturePath);
        int replacementProcessId = 0;
        ProcessExitObservation? replacementExit = null;
        try
        {
            string firstProjectPath = Path.Join(firstFixturePath, "First.csproj");
            string secondProjectPath = Path.Join(secondFixturePath, "Second.csproj");
            await WriteWorkspaceAsync(
                firstProjectPath,
                Path.Join(firstFixturePath, "First.cs"),
                "namespace First; public static class Marker { }",
                TestContext.CancellationToken).ConfigureAwait(false);
            await WriteWorkspaceAsync(
                secondProjectPath,
                Path.Join(secondFixturePath, "Second.cs"),
                "namespace Second; public static class Marker { }",
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession first = await StartLanguageServerAsync(
                repositoryRoot,
                serverWorkerPath,
                firstFixturePath,
                "csls-mcp-exit-first",
                TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable firstCleanup = first.ConfigureAwait(false);
            LspProcessSession second = await StartLanguageServerAsync(
                repositoryRoot,
                serverWorkerPath,
                secondFixturePath,
                "csls-mcp-exit-second",
                TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable secondCleanup = second.ConfigureAwait(false);
            McpProcessSession mcp = await McpProcessSession.StartAsync(
                repositoryRoot,
                mcpPath,
                mcpWorkerPath,
                serverWorkerPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable mcpCleanup = mcp.ConfigureAwait(false);
            McpClient client = mcp.Client;
            try
            {
                ControlSessionInfo firstAttached = await CallSessionAsync(
                    client,
                    new Dictionary<string, object?> { ["session"] = first.ProcessId },
                    TestContext.CancellationToken).ConfigureAwait(false);
                ControlSessionInfo secondAttached = await CallSessionAsync(
                    client,
                    new Dictionary<string, object?> { ["session"] = second.ProcessId },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(first.ProcessId, firstAttached.ProcessId);
                Assert.AreEqual(second.ProcessId, secondAttached.ProcessId);

                string firstDiagnostics = await first.ShutdownAsync(
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.DoesNotContain(
                    "Unhandled exception",
                    firstDiagnostics,
                    StringComparison.Ordinal);

                await AssertToolErrorAsync(
                    client,
                    new Dictionary<string, object?> { ["session"] = first.ProcessId },
                    "selected csls session disconnected",
                    TestContext.CancellationToken).ConfigureAwait(false);

                ControlSessionInfo survivingSecond = await CallSessionAsync(
                    client,
                    new Dictionary<string, object?> { ["session"] = second.ProcessId },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(second.ProcessId, survivingSecond.ProcessId);
                Assert.AreEqual(
                    secondFixturePath,
                    Assert.ContainsSingle(survivingSecond.WorkspaceRoots));

                ControlSessionInfo replacement = await CallSessionAsync(
                    client,
                    new Dictionary<string, object?> { ["workspace"] = firstProjectPath },
                    TestContext.CancellationToken).ConfigureAwait(false);
                replacementProcessId = replacement.ProcessId;
                replacementExit = ProcessExitWaiter.Observe(replacementProcessId);
                Assert.AreNotEqual(first.ProcessId, replacement.ProcessId);
                Assert.AreNotEqual(second.ProcessId, replacement.ProcessId);
                Assert.AreEqual(
                    firstProjectPath,
                    Assert.ContainsSingle(replacement.WorkspaceRoots));

                ControlSessionInfo secondAfterReplacement = await CallSessionAsync(
                    client,
                    new Dictionary<string, object?> { ["session"] = second.ProcessId },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(second.ProcessId, secondAfterReplacement.ProcessId);
            }
            finally
            {
                await DisconnectMcpAsync(
                    mcp,
                    TestContext.CancellationToken).ConfigureAwait(false);
            }

            await ProcessExitWaiter.WaitAsync(
                replacementExit ?? throw new InvalidOperationException(
                    "The later workspace request did not create a replacement session."),
                TimeSpan.FromSeconds(10),
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertFileDeletedAsync(
                ControlEndpoint.GetSocketPath(replacementProcessId),
                TimeSpan.FromSeconds(10),
                TestContext.CancellationToken).ConfigureAwait(false);

            var secondControlClient = new ControlRpcClient(
                ControlEndpoint.GetSocketPath(second.ProcessId));
            await using ConfiguredAsyncDisposable secondControlCleanup =
                secondControlClient.ConfigureAwait(false);
            ControlSessionInfo survivingAttached = await secondControlClient.GetSessionAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(second.ProcessId, survivingAttached.ProcessId);
            string secondDiagnostics = await second.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "Unhandled exception",
                secondDiagnostics,
                StringComparison.Ordinal);
        }
        finally
        {
            if (replacementExit is ProcessExitObservation exit)
            {
                await ProcessExitWaiter.WaitAsync(
                    exit,
                    TimeSpan.FromSeconds(10),
                    TestContext.CancellationToken).ConfigureAwait(false);
            }

            await DirectoryReleaseWaiter.DeleteAsync(
                firstFixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await DirectoryReleaseWaiter.DeleteAsync(
                secondFixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cancels one blocked readiness waiter without corrupting shared acquisition or another live target.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task CanceledWorkspaceReadinessPreservesSharedAcquisitionAndSecondTarget()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        (string serverWorkerPath, string mcpPath, string mcpWorkerPath) =
            ResolveProductPaths(repositoryRoot);
        string processHostPath = EditorToolResolver.ResolveTestProcessHost(repositoryRoot);
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-mcp-readiness-{Guid.NewGuid():N}");
        string secondFixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-mcp-readiness-second-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        Directory.CreateDirectory(secondFixturePath);
        string buildStartedPath = Path.Join(fixturePath, "build-started.marker");
        string buildReleasePath = Path.Join(fixturePath, "build-release.marker");
        int transientProcessId = 0;
        ProcessExitObservation? transientExit = null;
        try
        {
            string projectPath = Path.Join(fixturePath, "Blocked.csproj");
            string documentPath = Path.Join(fixturePath, "Program.cs");
            string secondProjectPath = Path.Join(secondFixturePath, "Second.csproj");
            string secondDocumentPath = Path.Join(secondFixturePath, "Second.cs");
            await File.WriteAllTextAsync(
                projectPath,
                CreateBlockedProjectText(
                    processHostPath,
                    buildStartedPath,
                    buildReleasePath),
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                "namespace Blocked; public static class Marker { }",
                TestContext.CancellationToken).ConfigureAwait(false);
            await WriteWorkspaceAsync(
                secondProjectPath,
                secondDocumentPath,
                "namespace Second; public static class Marker { }",
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession second = await StartLanguageServerAsync(
                repositoryRoot,
                serverWorkerPath,
                secondFixturePath,
                "csls-mcp-readiness-second",
                TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable secondCleanup = second.ConfigureAwait(false);
            McpProcessSession mcp = await McpProcessSession.StartAsync(
                repositoryRoot,
                mcpPath,
                mcpWorkerPath,
                serverWorkerPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable mcpCleanup = mcp.ConfigureAwait(false);
            McpClient client = mcp.Client;
            try
            {
                using var canceledSource = CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.CancellationToken);
                Task<CallToolResult> canceledReadiness = client.CallToolAsync(
                    "get_workspace_state",
                    new Dictionary<string, object?>
                    {
                        ["workspace"] = projectPath
                    },
                    cancellationToken: canceledSource.Token).AsTask();
                await FileTextWaiter.WaitAsync(
                    buildStartedPath,
                    "started",
                    TimeSpan.FromSeconds(60),
                    TestContext.CancellationToken).ConfigureAwait(false);

                Task<CallToolResult> sharedReadiness = client.CallToolAsync(
                    "get_workspace_state",
                    new Dictionary<string, object?>
                    {
                        ["workspace"] = projectPath
                    },
                    cancellationToken: TestContext.CancellationToken).AsTask();
                ControlSessionInfo transient = await CallSessionAsync(
                    client,
                    new Dictionary<string, object?> { ["workspace"] = projectPath },
                    TestContext.CancellationToken).ConfigureAwait(false);
                transientProcessId = transient.ProcessId;
                transientExit = ProcessExitWaiter.Observe(transientProcessId);

                await canceledSource.CancelAsync().ConfigureAwait(false);
                OperationCanceledException? cancellationException = null;
                try
                {
                    await canceledReadiness.ConfigureAwait(false);
                }
                catch (OperationCanceledException exception)
                {
                    cancellationException = exception;
                }

                Assert.IsNotNull(cancellationException);

                ControlSessionInfo secondDuringCancellation = await CallSessionAsync(
                    client,
                    new Dictionary<string, object?> { ["session"] = second.ProcessId },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(second.ProcessId, secondDuringCancellation.ProcessId);
                Assert.AreEqual(
                    secondFixturePath,
                    Assert.ContainsSingle(secondDuringCancellation.WorkspaceRoots));

                await File.WriteAllTextAsync(
                    buildReleasePath,
                    "release",
                    TestContext.CancellationToken).ConfigureAwait(false);
                CallToolResult sharedResult = await sharedReadiness.ConfigureAwait(false);
                Assert.IsNull(sharedResult.IsError);
                JsonElement workspaceSummary = McpAssertions.GetStructuredContent(sharedResult);
                Assert.AreEqual(
                    transientProcessId,
                    workspaceSummary.GetProperty("processId").GetInt32());
                Assert.IsGreaterThanOrEqualTo(
                    1,
                    workspaceSummary.GetProperty("projectCount").GetInt32());
                Assert.AreEqual(
                    $"csls://workspace/?session={transientProcessId}",
                    workspaceSummary.GetProperty("detailsUri").GetString());

                ControlSessionInfo repeated = await CallSessionAsync(
                    client,
                    new Dictionary<string, object?> { ["workspace"] = documentPath },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(transientProcessId, repeated.ProcessId);

                ControlSessionInfo secondAfterReadiness = await CallSessionAsync(
                    client,
                    new Dictionary<string, object?> { ["session"] = second.ProcessId },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(second.ProcessId, secondAfterReadiness.ProcessId);
            }
            finally
            {
                if (!File.Exists(buildReleasePath))
                {
                    await File.WriteAllTextAsync(
                        buildReleasePath,
                        "release",
                        TestContext.CancellationToken).ConfigureAwait(false);
                }

                await DisconnectMcpAsync(
                    mcp,
                    TestContext.CancellationToken).ConfigureAwait(false);
            }

            await ProcessExitWaiter.WaitAsync(
                transientExit ?? throw new InvalidOperationException(
                    "The blocked workspace did not create a transient process."),
                TimeSpan.FromSeconds(10),
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertFileDeletedAsync(
                ControlEndpoint.GetSocketPath(transientProcessId),
                TimeSpan.FromSeconds(10),
                TestContext.CancellationToken).ConfigureAwait(false);

            var secondControlClient = new ControlRpcClient(
                ControlEndpoint.GetSocketPath(second.ProcessId));
            await using ConfiguredAsyncDisposable secondControlCleanup =
                secondControlClient.ConfigureAwait(false);
            ControlSessionInfo survivingSecond = await secondControlClient.GetSessionAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(second.ProcessId, survivingSecond.ProcessId);
            string diagnostics = await second.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            if (!File.Exists(buildReleasePath))
            {
                await File.WriteAllTextAsync(
                    buildReleasePath,
                    "release",
                    TestContext.CancellationToken).ConfigureAwait(false);
            }

            if (transientExit is ProcessExitObservation exit)
            {
                await ProcessExitWaiter.WaitAsync(
                    exit,
                    TimeSpan.FromSeconds(10),
                    TestContext.CancellationToken).ConfigureAwait(false);
            }

            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await DirectoryReleaseWaiter.DeleteAsync(
                secondFixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
