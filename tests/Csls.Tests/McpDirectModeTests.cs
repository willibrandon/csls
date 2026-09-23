using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies MCP workspace selection against real transient csls language-server processes.
/// </summary>
[TestClass]
public sealed class McpDirectModeTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Reuses concurrent workspace targets, isolates workspaces, and cleans up owned sessions.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task WorkspaceSelectorsReuseAndOwnTransientLanguageServerLifetimes()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string serverWorkerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        string mcpPath = Environment.GetEnvironmentVariable("CSLS_TEST_MCP_PATH") ??
            Path.Join(
                artifactsRoot,
                "bin",
                "Csls.Mcp",
                "debug",
                "csls-mcp.dll");
        string mcpWorkerPath =
            Environment.GetEnvironmentVariable("CSLS_TEST_MCP_WORKER_PATH") ??
            Path.Join(
                artifactsRoot,
                "bin",
                "Csls.Mcp.Worker",
                "debug",
                "csls-mcp-worker.dll");
        Assert.IsTrue(File.Exists(serverWorkerPath));
        Assert.IsTrue(File.Exists(mcpPath));
        Assert.IsTrue(File.Exists(mcpWorkerPath));

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-mcp-direct-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        var transientProcesses = new List<(int ProcessId, ProcessExitObservation Exit)>();
        try
        {
            string firstWorkspacePath = Path.Join(fixturePath, "first");
            string secondWorkspacePath = Path.Join(fixturePath, "second");
            Directory.CreateDirectory(firstWorkspacePath);
            Directory.CreateDirectory(secondWorkspacePath);
            string projectPath = Path.Join(firstWorkspacePath, "Direct.csproj");
            string documentPath = Path.Join(firstWorkspacePath, "Program.cs");
            string secondProjectPath = Path.Join(secondWorkspacePath, "Other.csproj");
            string secondDocumentPath = Path.Join(secondWorkspacePath, "Other.cs");
            await File.WriteAllTextAsync(
                projectPath,
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                secondProjectPath,
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                secondDocumentPath,
                "Console.WriteLine(42);",
                TestContext.CancellationToken).ConfigureAwait(false);

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
                CallToolResult missingSelector = await client.CallToolAsync(
                    "get_session",
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                AssertToolError(missingSelector, "Specify exactly one target");
                CallToolResult multipleSelectors = await client.CallToolAsync(
                    "get_session",
                    new Dictionary<string, object?>
                    {
                        ["workspace"] = projectPath,
                        ["session"] = Environment.ProcessId
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                AssertToolError(multipleSelectors, "Specify exactly one target");
                CallToolResult invalidSession = await client.CallToolAsync(
                    "get_session",
                    new Dictionary<string, object?> { ["session"] = 0 },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                AssertToolError(invalidSession, "session must be a positive process identifier");
                CallToolResult invalidSocket = await client.CallToolAsync(
                    "get_session",
                    new Dictionary<string, object?> { ["socket"] = "relative.socket" },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                AssertToolError(invalidSocket, "socket must be an absolute path");
                CallToolResult missingWorkspace = await client.CallToolAsync(
                    "get_session",
                    new Dictionary<string, object?>
                    {
                        ["workspace"] = Path.Join(fixturePath, "missing")
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                AssertToolError(missingWorkspace, "workspace does not exist");

                Task<CallToolResult>[] concurrentRequests =
                [
                    .. Enumerable.Range(0, 4).Select(_ => client.CallToolAsync(
                        "get_session",
                        new Dictionary<string, object?> { ["workspace"] = projectPath },
                        cancellationToken: TestContext.CancellationToken).AsTask())
                ];
                CallToolResult[] sessionResults = await Task.WhenAll(concurrentRequests)
                    .ConfigureAwait(false);
                ControlSessionInfo[] sessions =
                [
                    .. sessionResults.Select(result =>
                    {
                        Assert.IsNull(result.IsError);
                        return McpAssertions.GetStructuredContent(result).Deserialize(
                            ControlJsonSerializerContext.Default.ControlSessionInfo)
                            ?? throw new InvalidDataException(
                                "MCP workspace mode returned no session information.");
                    })
                ];
                ControlSessionInfo session = sessions[0];
                Assert.HasCount(1, sessions.Select(static item => item.ProcessId).Distinct());
                transientProcesses.Add((
                    session.ProcessId,
                    ProcessExitWaiter.Observe(session.ProcessId)));
                Assert.AreNotEqual(Environment.ProcessId, session.ProcessId);
                Assert.AreEqual(projectPath, Assert.ContainsSingle(session.WorkspaceRoots));

                CallToolResult workspaceResult = await client.CallToolAsync(
                    "get_workspace_state",
                    new Dictionary<string, object?>
                    {
                        ["workspace"] = projectPath
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(workspaceResult.IsError);
                JsonElement workspace = McpAssertions.GetStructuredContent(workspaceResult);
                Assert.AreEqual(session.ProcessId, workspace.GetProperty("processId").GetInt32());
                Assert.AreEqual(1, workspace.GetProperty("projectCount").GetInt32());
                Assert.IsGreaterThanOrEqualTo(
                    1,
                    workspace.GetProperty("documentCount").GetInt32());
                Assert.AreEqual(
                    $"csls://workspace/?session={session.ProcessId}",
                    workspace.GetProperty("detailsUri").GetString());

                ReadResourceResult diagnosticResource = await client.ReadResourceAsync(
                    "csls://diagnostic/{?workspace,session,socket,path}",
                    new Dictionary<string, object?>
                    {
                        ["workspace"] = projectPath,
                        ["path"] = documentPath
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                DocumentDiagnosticReport diagnostics = JsonSerializer.Deserialize(
                    diagnosticResource.Contents.OfType<TextResourceContents>().Single().Text,
                    ControlJsonSerializerContext.Default.DocumentDiagnosticReport)
                    ?? throw new InvalidDataException(
                        "MCP direct mode returned no diagnostic resource.");
                Assert.Contains(
                    "CS0103",
                    diagnostics.Items?.Select(static diagnostic => diagnostic.Code) ?? []);

                CallToolResult secondSessionResult = await client.CallToolAsync(
                    "get_session",
                    new Dictionary<string, object?> { ["workspace"] = secondProjectPath },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(secondSessionResult.IsError);
                ControlSessionInfo secondSession = McpAssertions.GetStructuredContent(secondSessionResult)
                    .Deserialize(ControlJsonSerializerContext.Default.ControlSessionInfo)
                    ?? throw new InvalidDataException(
                        "MCP workspace mode returned no second session information.");
                transientProcesses.Add((
                    secondSession.ProcessId,
                    ProcessExitWaiter.Observe(secondSession.ProcessId)));
                Assert.AreNotEqual(session.ProcessId, secondSession.ProcessId);
                Assert.AreEqual(
                    secondProjectPath,
                    Assert.ContainsSingle(secondSession.WorkspaceRoots));

                CallToolResult listedSessionsResult = await client.CallToolAsync(
                    "list_sessions",
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(listedSessionsResult.IsError);
                JsonElement listedSessions = McpAssertions.GetStructuredContent(listedSessionsResult);
                Assert.AreEqual(JsonValueKind.Array, listedSessions.ValueKind);
                int[] listedProcessIds =
                [
                    .. listedSessions.EnumerateArray().Select(item =>
                        item.GetProperty("processId").GetInt32())
                ];
                Assert.Contains(session.ProcessId, listedProcessIds);
                Assert.Contains(secondSession.ProcessId, listedProcessIds);
            }
            finally
            {
                string diagnostics = await mcp.DisconnectAsync(
                    TimeSpan.FromSeconds(30),
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.DoesNotContain(
                    "Unhandled exception",
                    diagnostics,
                    StringComparison.Ordinal);
            }

            Assert.HasCount(2, transientProcesses);
            foreach ((int processId, ProcessExitObservation exit) in transientProcesses)
            {
                await ProcessExitWaiter.WaitAsync(
                    exit,
                    TimeSpan.FromSeconds(10),
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsFalse(File.Exists(ControlEndpoint.GetSocketPath(processId)));
            }
        }
        finally
        {
            foreach ((_, ProcessExitObservation exit) in transientProcesses)
            {
                await ProcessExitWaiter.WaitAsync(
                    exit,
                    TimeSpan.FromSeconds(10),
                    TestContext.CancellationToken).ConfigureAwait(false);
            }

            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static void AssertToolError(CallToolResult result, string expectedMessage)
    {
        Assert.IsTrue(result.IsError);
        Assert.IsNull(result.StructuredContent);
        string text = result.Content.OfType<TextContentBlock>().Single().Text;
        Assert.Contains(expectedMessage, text, StringComparison.Ordinal);
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
        Console.WriteLine(Missing);
        """;
}
