using Csls.Control;
using Csls.Control.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies graceful language-server signal handling through real operating-system processes.
/// </summary>
[TestClass]
public sealed class LanguageServerSignalTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Stops a Ready worker promptly on SIGTERM without corrupting MCP-owned worker lifetimes.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task ReadyLanguageServerTerminatesPromptlyOnSigtermAndPreservesMcpOwnershipBoundaries()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string serverWorkerPath = EditorToolResolver.ResolveServerWorker(repositoryRoot);
        string mcpPath = Environment.GetEnvironmentVariable("CSLS_TEST_MCP_PATH") ??
            Path.Join(artifactsRoot, "bin", "Csls.Mcp", "debug", "csls-mcp.dll");
        string mcpWorkerPath =
            Environment.GetEnvironmentVariable("CSLS_TEST_MCP_WORKER_PATH") ??
            Path.Join(artifactsRoot, "bin", "Csls.Mcp.Worker", "debug", "csls-mcp-worker.dll");
        Assert.IsTrue(File.Exists(serverWorkerPath), serverWorkerPath);
        Assert.IsTrue(File.Exists(mcpPath), mcpPath);
        Assert.IsTrue(File.Exists(mcpWorkerPath), mcpWorkerPath);

        string fixtureRoot = Path.Join(
            Path.GetTempPath(),
            $"csls-sigterm-{Guid.NewGuid():N}");
        string attachedRoot = Path.Join(fixtureRoot, "attached");
        string ownedRoot = Path.Join(fixtureRoot, "owned");
        Directory.CreateDirectory(attachedRoot);
        Directory.CreateDirectory(ownedRoot);
        string attachedProjectPath = Path.Join(attachedRoot, "Attached.csproj");
        string ownedProjectPath = Path.Join(ownedRoot, "Owned.csproj");
        await WriteWorkspaceAsync(
            attachedProjectPath,
            Path.Join(attachedRoot, "Attached.cs"),
            "public static class AttachedType { public static int Value => 1; }",
            TestContext.CancellationToken).ConfigureAwait(false);
        await WriteWorkspaceAsync(
            ownedProjectPath,
            Path.Join(ownedRoot, "Owned.cs"),
            "public static class OwnedType { public static int Value => 2; }",
            TestContext.CancellationToken).ConfigureAwait(false);

        ProcessExitObservation? ownedExit = null;
        int ownedProcessId = 0;
        try
        {
            LspProcessSession attached = await LspProcessSession.StartAsync(
                "csls-sigterm-attached",
                EditorToolResolver.ResolveDotNetHost(),
                [serverWorkerPath],
                repositoryRoot).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable attachedCleanup =
                attached.ConfigureAwait(false);
            await attached.InitializeAsync(
                attachedProjectPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await attached.CompleteInitializationAsync().ConfigureAwait(false);
            ControlSessionInfo readyAttached = await ControlSessionWaiter.WaitForRunningAsync(
                attachedProjectPath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken,
                expectedProcessId: attached.ProcessId).ConfigureAwait(false);
            Assert.AreEqual("Running", readyAttached.LifecycleState);
            Assert.AreEqual("Ready", readyAttached.WorkspacePhase);
            string attachedSocketPath = ControlEndpoint.GetSocketPath(attached.ProcessId);
            Assert.IsTrue(File.Exists(attachedSocketPath));

            McpProcessSession mcp = await McpProcessSession.StartAsync(
                repositoryRoot,
                mcpPath,
                mcpWorkerPath,
                serverWorkerPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable mcpCleanup = mcp.ConfigureAwait(false);
            McpClient client = mcp.Client;
            ControlSessionInfo attachedThroughMcp = await GetSessionAsync(
                client,
                new Dictionary<string, object?> { ["session"] = attached.ProcessId },
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(attached.ProcessId, attachedThroughMcp.ProcessId);
            Assert.AreEqual(attachedProjectPath, Assert.ContainsSingle(
                attachedThroughMcp.WorkspaceRoots));

            ControlSessionInfo owned = await GetSessionAsync(
                client,
                new Dictionary<string, object?> { ["workspace"] = ownedProjectPath },
                TestContext.CancellationToken).ConfigureAwait(false);
            ownedProcessId = owned.ProcessId;
            ownedExit = ProcessExitWaiter.Observe(ownedProcessId);
            Assert.AreNotEqual(attached.ProcessId, ownedProcessId);
            Assert.AreEqual(ownedProjectPath, Assert.ContainsSingle(owned.WorkspaceRoots));
            ControlSessionInfo readyOwned = await ControlSessionWaiter.WaitForRunningAsync(
                ownedProjectPath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken,
                expectedProcessId: ownedProcessId).ConfigureAwait(false);
            Assert.AreEqual(ownedProcessId, readyOwned.ProcessId);
            Assert.AreEqual("Running", readyOwned.LifecycleState);
            Assert.AreEqual("Ready", readyOwned.WorkspacePhase);
            string ownedSocketPath = ControlEndpoint.GetSocketPath(ownedProcessId);
            Assert.IsTrue(File.Exists(ownedSocketPath));

            await SendSigtermAsync(
                attached.ProcessId,
                TestContext.CancellationToken).ConfigureAwait(false);
            string signalDiagnostics = await attached.WaitForExitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "Unhandled exception",
                signalDiagnostics,
                StringComparison.Ordinal);
            Assert.IsFalse(File.Exists(attachedSocketPath));

            ControlSessionInfo ownedAfterSignal = await GetSessionAsync(
                client,
                new Dictionary<string, object?> { ["session"] = ownedProcessId },
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(ownedProcessId, ownedAfterSignal.ProcessId);
            Assert.AreEqual(ownedProjectPath, Assert.ContainsSingle(
                ownedAfterSignal.WorkspaceRoots));
            Assert.AreEqual("Running", ownedAfterSignal.LifecycleState);
            Assert.AreEqual("Ready", ownedAfterSignal.WorkspacePhase);
            Assert.IsTrue(File.Exists(ownedSocketPath));

            string mcpDiagnostics = await mcp.DisconnectAsync(
                TimeSpan.FromSeconds(30),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "Unhandled exception",
                mcpDiagnostics,
                StringComparison.Ordinal);
            await ProcessExitWaiter.WaitAsync(
                ownedExit.Value,
                TimeSpan.FromSeconds(10),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsFalse(File.Exists(ownedSocketPath));
            ownedExit = null;
        }
        finally
        {
            if (ownedExit is ProcessExitObservation exit)
            {
                await ProcessExitWaiter.WaitAsync(
                    exit,
                    TimeSpan.FromSeconds(10),
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsFalse(File.Exists(ControlEndpoint.GetSocketPath(ownedProcessId)));
            }

            await DirectoryReleaseWaiter.DeleteAsync(
                fixtureRoot,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static async Task<ControlSessionInfo> GetSessionAsync(
        McpClient client,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        CallToolResult result = await client.CallToolAsync(
            "get_session",
            arguments,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Assert.IsNull(result.IsError);
        return McpAssertions.GetStructuredContent(result).Deserialize(
            ControlJsonSerializerContext.Default.ControlSessionInfo)
            ?? throw new InvalidDataException("MCP returned no session information.");
    }

    private static async Task SendSigtermAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        string killPath = File.Exists("/bin/kill") ? "/bin/kill" : "/usr/bin/kill";
        Assert.IsTrue(File.Exists(killPath), "The Unix kill executable was not found.");
        var startInfo = new ProcessStartInfo
        {
            FileName = killPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-TERM");
        startInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        using Process signal = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Unix SIGTERM process did not start.");
        Task<string> outputTask = signal.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> errorTask = signal.StandardError.ReadToEndAsync(CancellationToken.None);
        await signal.WaitForExitAsync(cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
            .ConfigureAwait(false);
        string output = await outputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        Assert.AreEqual(0, signal.ExitCode, error);
        Assert.IsEmpty(output);
        Assert.IsEmpty(error);
    }

    private static async Task WriteWorkspaceAsync(
        string projectPath,
        string documentPath,
        string documentText,
        CancellationToken cancellationToken)
    {
        const string projectText = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(
            projectPath,
            projectText,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            documentPath,
            documentText,
            cancellationToken).ConfigureAwait(false);
    }
}
