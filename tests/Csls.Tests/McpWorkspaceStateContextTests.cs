using Csls.Control;
using Csls.Control.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies that MCP workspace state is compact by default and exposes intentional detail.
/// </summary>
[TestClass]
public sealed class McpWorkspaceStateContextTests
{
    private const int MaximumSummaryBytes = 2_048;
    private static readonly string[] s_summaryPropertyNames =
    [
        "acceptedRequestCount",
        "activeRequestCount",
        "buildHostCount",
        "cacheCount",
        "completedRequestCount",
        "detailsUri",
        "documentCount",
        "isMutationActive",
        "isStopping",
        "lifecycleState",
        "processId",
        "projectCount",
        "queuedRequestCount",
        "retainedLogCount",
        "workspaceCount",
        "workspaceGeneration",
        "workspacePhase",
        "workspaceRootCount"
    ];

    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Returns a fixed compact overview for small and large workspaces with a readable detail link.
    /// </summary>
    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task WorkspaceStateReturnsFixedCompactOverviewAndResourceLinkForDetail()
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
            $"csls-mcp-context-{Guid.NewGuid():N}");
        string smallRoot = Path.Join(fixtureRoot, "small");
        string largeRoot = Path.Join(fixtureRoot, "large");
        Directory.CreateDirectory(smallRoot);
        Directory.CreateDirectory(largeRoot);
        string smallProjectPath = Path.Join(smallRoot, "Small.csproj");
        string smallDocumentPath = Path.Join(smallRoot, "Small.cs");
        string largeProjectPath = Path.Join(largeRoot, "Large.csproj");
        string diagnosticDocumentPath = Path.Join(largeRoot, "Broken.cs");
        await WriteProjectAsync(
            smallProjectPath,
            TestContext.CancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            smallDocumentPath,
            "public static class SmallType { public static int Value => 1; }",
            TestContext.CancellationToken).ConfigureAwait(false);
        await WriteProjectAsync(
            largeProjectPath,
            TestContext.CancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            diagnosticDocumentPath,
            "public static class BrokenType { public static int Value => MissingName; }",
            TestContext.CancellationToken).ConfigureAwait(false);
        const int generatedDocumentCount = 64;
        for (int index = 0; index < generatedDocumentCount; index++)
        {
            await File.WriteAllTextAsync(
                Path.Join(largeRoot, $"Type{index:D2}.cs"),
                $"public static class Type{index:D2} {{ public const int Value = {index}; }}",
                TestContext.CancellationToken).ConfigureAwait(false);
        }

        var ownedProcesses = new List<(int ProcessId, ProcessExitObservation Exit)>();
        try
        {
            McpProcessSession mcp = await McpProcessSession.StartAsync(
                repositoryRoot,
                mcpPath,
                mcpWorkerPath,
                serverWorkerPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable mcpCleanup = mcp.ConfigureAwait(false);
            McpClient client = mcp.Client;

            IList<McpClientTool> tools = await client.ListToolsAsync(
                cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
            McpClientTool workspaceTool = Assert.ContainsSingle(
                tools.Where(static tool => tool.Name == "get_workspace_state"));
            AssertWorkspaceStateSchemas(workspaceTool);

            CallToolResult smallResult = await client.CallToolAsync(
                "get_workspace_state",
                new Dictionary<string, object?> { ["workspace"] = smallProjectPath },
                cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
            (JsonElement smallSummary, ResourceLinkBlock smallDetailsLink) =
                AssertCompactSummary(smallResult);
            int smallProcessId = smallSummary.GetProperty("processId").GetInt32();
            ownedProcesses.Add((smallProcessId, ProcessExitWaiter.Observe(smallProcessId)));
            AssertSummaryIdentity(
                smallSummary,
                expectedProjectCount: 1,
                minimumDocumentCount: 1);
            Assert.AreEqual(
                $"csls://workspace/?session={smallProcessId}",
                smallDetailsLink.Uri);

            CallToolResult largeResult = await client.CallToolAsync(
                "get_workspace_state",
                new Dictionary<string, object?> { ["workspace"] = largeProjectPath },
                cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
            (JsonElement largeSummary, ResourceLinkBlock largeDetailsLink) =
                AssertCompactSummary(largeResult);
            int largeProcessId = largeSummary.GetProperty("processId").GetInt32();
            ownedProcesses.Add((largeProcessId, ProcessExitWaiter.Observe(largeProcessId)));
            Assert.AreNotEqual(smallProcessId, largeProcessId);
            AssertSummaryIdentity(
                largeSummary,
                expectedProjectCount: 1,
                minimumDocumentCount: generatedDocumentCount + 1);
            Assert.AreEqual(
                $"csls://workspace/?session={largeProcessId}",
                largeDetailsLink.Uri);
            Assert.IsGreaterThan(
                smallSummary.GetProperty("documentCount").GetInt32(),
                largeSummary.GetProperty("documentCount").GetInt32());

            ReadResourceResult detailsResult = await client.ReadResourceAsync(
                new Uri(largeDetailsLink.Uri, UriKind.Absolute),
                cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
            TextResourceContents detailContents = Assert.ContainsSingle(
                detailsResult.Contents.OfType<TextResourceContents>());
            ControlDashboardSnapshot details = JsonSerializer.Deserialize(
                detailContents.Text,
                ControlJsonSerializerContext.Default.ControlDashboardSnapshot)
                ?? throw new InvalidDataException("The linked workspace resource was empty.");
            Assert.AreEqual(largeProcessId, details.Session.ProcessId);
            Assert.HasCount(
                largeSummary.GetProperty("workspaceRootCount").GetInt32(),
                details.Session.WorkspaceRoots);
            Assert.HasCount(
                largeSummary.GetProperty("workspaceCount").GetInt32(),
                details.Workspaces);
            Assert.HasCount(
                largeSummary.GetProperty("projectCount").GetInt32(),
                details.Projects);
            Assert.HasCount(
                largeSummary.GetProperty("documentCount").GetInt32(),
                details.Documents);
            Assert.HasCount(
                largeSummary.GetProperty("buildHostCount").GetInt32(),
                details.BuildHosts);
            Assert.HasCount(
                largeSummary.GetProperty("cacheCount").GetInt32(),
                details.Caches);
            Assert.Contains(
                largeProjectPath,
                details.Projects.Select(static project => project.FilePath));
            Assert.Contains(
                diagnosticDocumentPath,
                details.Documents.Select(static document => document.FilePath));
            Assert.Contains(
                Path.Join(largeRoot, $"Type{generatedDocumentCount - 1:D2}.cs"),
                details.Documents.Select(static document => document.FilePath));
            Assert.IsGreaterThanOrEqualTo(generatedDocumentCount + 1, details.Documents.Count);

            string diagnostics = await mcp.DisconnectAsync(
                TimeSpan.FromSeconds(30),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "Unhandled exception",
                diagnostics,
                StringComparison.Ordinal);
        }
        finally
        {
            foreach ((int processId, ProcessExitObservation exit) in ownedProcesses)
            {
                await ProcessExitWaiter.WaitAsync(
                    exit,
                    TimeSpan.FromSeconds(10),
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsFalse(File.Exists(ControlEndpoint.GetSocketPath(processId)));
            }

            await DirectoryReleaseWaiter.DeleteAsync(
                fixtureRoot,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Keeps compact workspace inspection from starting analyzer execution.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task WorkspaceStateDoesNotExecuteProjectAnalyzers()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string serverWorkerPath = EditorToolResolver.ResolveServerWorker(repositoryRoot);
        string mcpPath = Environment.GetEnvironmentVariable("CSLS_TEST_MCP_PATH") ??
            Path.Join(artifactsRoot, "bin", "Csls.Mcp", "debug", "csls-mcp.dll");
        string mcpWorkerPath =
            Environment.GetEnvironmentVariable("CSLS_TEST_MCP_WORKER_PATH") ??
            Path.Join(artifactsRoot, "bin", "Csls.Mcp.Worker", "debug", "csls-mcp-worker.dll");
        AnalyzerExecutionProbeFixture fixture = await AnalyzerExecutionProbeFixture.CreateAsync(
            repositoryRoot,
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        McpProcessSession mcp = await McpProcessSession.StartAsync(
            repositoryRoot,
            mcpPath,
            mcpWorkerPath,
            serverWorkerPath,
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable mcpCleanup = mcp.ConfigureAwait(false);
        bool analyzerStarted = false;
        try
        {
            Task<CallToolResult> workspaceStateTask = mcp.Client.CallToolAsync(
                "get_workspace_state",
                new Dictionary<string, object?> { ["workspace"] = fixture.RootPath },
                cancellationToken: TestContext.CancellationToken).AsTask();
            while (!workspaceStateTask.IsCompleted)
            {
                string[] events = await fixture.ReadEventsAsync(
                    TestContext.CancellationToken).ConfigureAwait(false);
                if (events.Contains("started", StringComparer.Ordinal))
                {
                    analyzerStarted = true;
                    await fixture.ReleaseAsync(TestContext.CancellationToken).ConfigureAwait(false);
                    break;
                }

                await Task.Delay(50, TestContext.CancellationToken).ConfigureAwait(false);
            }

            CallToolResult result = await workspaceStateTask.ConfigureAwait(false);
            Assert.IsNull(result.IsError);
            Assert.IsFalse(
                analyzerStarted,
                "Compact MCP workspace inspection started project-wide analyzer execution.");
            Assert.IsEmpty(await fixture.ReadEventsAsync(
                TestContext.CancellationToken).ConfigureAwait(false));
        }
        finally
        {
            await fixture.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        }

        string diagnostics = await mcp.DisconnectAsync(
            TimeSpan.FromSeconds(30),
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
    }

    private static void AssertWorkspaceStateSchemas(McpClientTool workspaceTool)
    {
        JsonElement inputSchema = workspaceTool.ProtocolTool.InputSchema;
        JsonElement inputProperties = inputSchema.GetProperty("properties");
        Assert.IsFalse(inputProperties.TryGetProperty("includeDiagnostics", out _));

        JsonElement outputSchema = workspaceTool.ProtocolTool.OutputSchema
            ?? throw new InvalidDataException("get_workspace_state published no output schema.");
        Assert.AreEqual("object", outputSchema.GetProperty("type").GetString());
        JsonElement outputProperties = outputSchema.GetProperty("properties");
        string[] actualPropertyNames =
        [
            .. outputProperties.EnumerateObject()
                .Select(static property => property.Name)
                .OrderBy(static name => name, StringComparer.Ordinal)
        ];
        Assert.HasCount(s_summaryPropertyNames.Length, actualPropertyNames);
        foreach (string expectedPropertyName in s_summaryPropertyNames)
        {
            Assert.Contains(expectedPropertyName, actualPropertyNames);
        }
    }

    private static (JsonElement Summary, ResourceLinkBlock DetailsLink) AssertCompactSummary(
        CallToolResult result)
    {
        Assert.IsNull(result.IsError);
        JsonElement summary = McpAssertions.GetStructuredContent(result);
        Assert.AreEqual(JsonValueKind.Object, summary.ValueKind);
        JsonProperty[] properties = [.. summary.EnumerateObject()];
        Assert.HasCount(s_summaryPropertyNames.Length, properties);
        foreach (string expectedPropertyName in s_summaryPropertyNames)
        {
            Assert.IsTrue(
                summary.TryGetProperty(expectedPropertyName, out _),
                $"The compact summary omitted {expectedPropertyName}.");
        }

        foreach (JsonProperty property in properties)
        {
            Assert.AreNotEqual(
                JsonValueKind.Array,
                property.Value.ValueKind,
                $"The compact summary expanded {property.Name} into an array.");
            Assert.AreNotEqual(
                JsonValueKind.Object,
                property.Value.ValueKind,
                $"The compact summary expanded {property.Name} into an object.");
        }

        int serializedBytes = Encoding.UTF8.GetByteCount(summary.GetRawText());
        Assert.IsLessThanOrEqualTo(MaximumSummaryBytes, serializedBytes);
        Assert.HasCount(2, result.Content);
        TextContentBlock text = Assert.ContainsSingle(result.Content.OfType<TextContentBlock>());
        ResourceLinkBlock detailsLink = Assert.ContainsSingle(
            result.Content.OfType<ResourceLinkBlock>());
        Assert.AreEqual(summary.GetRawText(), text.Text);
        Assert.AreEqual(summary.GetProperty("detailsUri").GetString(), detailsLink.Uri);
        Assert.IsNotEmpty(detailsLink.Name);
        Assert.AreEqual("application/json", detailsLink.MimeType);
        return (summary, detailsLink);
    }

    private static void AssertSummaryIdentity(
        JsonElement summary,
        int expectedProjectCount,
        int minimumDocumentCount)
    {
        Assert.IsGreaterThan(0, summary.GetProperty("processId").GetInt32());
        Assert.AreEqual("Running", summary.GetProperty("lifecycleState").GetString());
        Assert.AreEqual("Ready", summary.GetProperty("workspacePhase").GetString());
        Assert.IsGreaterThanOrEqualTo(1L, summary.GetProperty("workspaceGeneration").GetInt64());
        Assert.AreEqual(1, summary.GetProperty("workspaceRootCount").GetInt32());
        Assert.AreEqual(1, summary.GetProperty("workspaceCount").GetInt32());
        Assert.AreEqual(expectedProjectCount, summary.GetProperty("projectCount").GetInt32());
        Assert.IsGreaterThanOrEqualTo(
            minimumDocumentCount,
            summary.GetProperty("documentCount").GetInt32());
        Assert.IsGreaterThanOrEqualTo(
            0L,
            summary.GetProperty("acceptedRequestCount").GetInt64());
        Assert.IsGreaterThanOrEqualTo(
            0L,
            summary.GetProperty("completedRequestCount").GetInt64());
        Assert.IsGreaterThanOrEqualTo(0, summary.GetProperty("activeRequestCount").GetInt32());
        Assert.IsGreaterThanOrEqualTo(0, summary.GetProperty("queuedRequestCount").GetInt32());
        Assert.IsFalse(summary.GetProperty("isStopping").GetBoolean());
        Assert.IsGreaterThanOrEqualTo(1, summary.GetProperty("buildHostCount").GetInt32());
        Assert.IsGreaterThanOrEqualTo(1, summary.GetProperty("cacheCount").GetInt32());
        Assert.IsGreaterThanOrEqualTo(0, summary.GetProperty("retainedLogCount").GetInt32());
    }

    private static Task WriteProjectAsync(string projectPath, CancellationToken cancellationToken)
    {
        const string projectText = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """;
        return File.WriteAllTextAsync(projectPath, projectText, cancellationToken);
    }
}
