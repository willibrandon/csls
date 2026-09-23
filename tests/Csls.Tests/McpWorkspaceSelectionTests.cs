using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies target selection, failure isolation, cancellation, and ownership through real MCP processes.
/// </summary>
[TestClass]
public sealed partial class McpWorkspaceSelectionTests
{
    private static readonly string[] s_expectedResourceTemplates =
    [
        "csls://diagnostic/{?workspace,session,socket,path}",
        "csls://document/{?workspace,session,socket,path}",
        "csls://project/{?workspace,session,socket,path}",
        "csls://session/{?workspace,session,socket}",
        "csls://workspace/{?workspace,session,socket}"
    ];
    private static readonly string[] s_expectedPrompts =
    [
        "diagnose_csharp",
        "explain_symbol",
        "refactor_csharp",
        "review_csharp",
        "troubleshoot_csls"
    ];

    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Starts the bare MCP server and discovers its complete targeted surface before starting a language server.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task BareServerStartsAndPublishesTargetedSurface()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        (_, string mcpPath, string mcpWorkerPath) = ResolveProductPaths(repositoryRoot);
        McpClient client = await CreateMcpClientAsync(
            repositoryRoot,
            mcpPath,
            mcpWorkerPath,
            "csls-mcp-bare-surface",
            serverWorkerPath: null,
            TestContext.CancellationToken).ConfigureAwait(false);
        try
        {
            Assert.AreEqual("csls", client.ServerInfo.Name);
            string instructions = client.ServerInstructions
                ?? throw new InvalidDataException("The bare MCP server published no instructions.");
            Assert.Contains("Except for list_sessions", instructions, StringComparison.Ordinal);
            Assert.Contains(
                "every tool and resource requires exactly one",
                instructions,
                StringComparison.Ordinal);
            Assert.Contains(
                "workspace, session, or socket",
                instructions,
                StringComparison.Ordinal);
            IList<McpClientTool> tools = await client.ListToolsAsync(
                cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
            string[] expectedTargetTools =
            [
                "get_session",
                "get_hover",
                "get_diagnostics",
                "get_completion",
                "get_definition",
                "get_declaration",
                "get_type_definition",
                "get_implementation",
                "get_selection_range",
                "get_document_highlights",
                "get_references",
                "get_document_symbols",
                "search_workspace_symbols",
                "get_signature_help",
                "preview_rename",
                "preview_formatting",
                "apply_edit_plan",
                "get_code_actions",
                "get_workspace_state",
                "restore_workspace",
                "reload_workspace",
                "restart_build_hosts",
                "clear_caches",
                "list_requests",
                "cancel_request",
                "start_trace",
                "stop_trace"
            ];
            Assert.HasCount(expectedTargetTools.Length + 1, tools);
            IEnumerable<(McpClientTool Tool, ToolAnnotations Annotations)>
                toolsWithAnnotations = tools.Select(tool =>
                {
                    ToolAnnotations annotations = tool.ProtocolTool.Annotations
                        ?? throw new InvalidDataException(
                            $"Tool {tool.Name} published no MCP behavior annotations.");
                    return (tool, annotations);
                });
            foreach ((McpClientTool tool, ToolAnnotations annotations) in toolsWithAnnotations)
            {
                Assert.IsNotNull(annotations.ReadOnlyHint, tool.Name);
                Assert.IsNotNull(annotations.DestructiveHint, tool.Name);
                Assert.IsNotNull(annotations.IdempotentHint, tool.Name);
                Assert.IsNotNull(annotations.OpenWorldHint, tool.Name);
            }

            McpClientTool[] targetTools =
            [
                .. expectedTargetTools.Select(toolName => Assert.ContainsSingle(
                    tools.Where(candidate => candidate.Name == toolName)))
            ];
            foreach (McpClientTool tool in targetTools)
            {
                Assert.IsNotNull(tool.ProtocolTool.OutputSchema);
                JsonElement properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
                Assert.IsTrue(
                    properties.TryGetProperty("workspace", out JsonElement workspace));
                Assert.IsTrue(properties.TryGetProperty("session", out JsonElement session));
                Assert.IsTrue(properties.TryGetProperty("socket", out JsonElement socket));
                Assert.AreEqual("string", GetSchemaType(workspace));
                Assert.AreEqual("integer", GetSchemaType(session));
                Assert.AreEqual("string", GetSchemaType(socket));
                if (tool.ProtocolTool.InputSchema.TryGetProperty(
                        "required",
                        out JsonElement required))
                {
                    string[] requiredNames =
                    [
                        .. required.EnumerateArray().Select(static item =>
                            item.GetString() ?? string.Empty)
                    ];
                    Assert.DoesNotContain("workspace", requiredNames);
                    Assert.DoesNotContain("session", requiredNames);
                    Assert.DoesNotContain("socket", requiredNames);
                }
            }

            McpClientTool sessionsTool = Assert.ContainsSingle(
                tools.Where(static tool => tool.Name == "list_sessions"));
            Assert.IsNotNull(sessionsTool.ProtocolTool.OutputSchema);
            JsonElement sessionsProperties = sessionsTool.ProtocolTool.InputSchema
                .GetProperty("properties");
            Assert.IsFalse(sessionsProperties.TryGetProperty("workspace", out _));
            Assert.IsFalse(sessionsProperties.TryGetProperty("session", out _));
            Assert.IsFalse(sessionsProperties.TryGetProperty("socket", out _));
            AssertToolAnnotations(
                sessionsTool,
                readOnly: true,
                destructive: false,
                idempotent: true,
                openWorld: false);
            AssertToolAnnotations(
                tools.Single(static tool => tool.Name == "get_session"),
                readOnly: true,
                destructive: false,
                idempotent: true,
                openWorld: false);
            AssertToolAnnotations(
                tools.Single(static tool => tool.Name == "apply_edit_plan"),
                readOnly: false,
                destructive: true,
                idempotent: false,
                openWorld: false);
            AssertToolAnnotations(
                tools.Single(static tool => tool.Name == "clear_caches"),
                readOnly: false,
                destructive: true,
                idempotent: true,
                openWorld: false);
            AssertToolAnnotations(
                tools.Single(static tool => tool.Name == "cancel_request"),
                readOnly: false,
                destructive: true,
                idempotent: true,
                openWorld: false);

            IList<McpClientResource> resources = await client.ListResourcesAsync(
                cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsEmpty(resources);
            IList<McpClientResourceTemplate> templates = await client
                .ListResourceTemplatesAsync(cancellationToken: TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreSequenceEqual(
                s_expectedResourceTemplates,
                templates
                    .Select(static template => template.UriTemplate)
                    .Order(StringComparer.Ordinal)
                    .ToArray());
            IList<McpClientPrompt> prompts = await client.ListPromptsAsync(
                cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                s_expectedPrompts,
                prompts.Select(static prompt => prompt.Name).Order(StringComparer.Ordinal).ToArray());
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rejects removed startup target arguments without compatibility parsing or fallback behavior.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task StartupTargetArgumentsAreRejectedWithoutCompatibilityFallback()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        (_, string mcpPath, string mcpWorkerPath) = ResolveProductPaths(repositoryRoot);
        await AssertStartupArgumentRejectedAsync(
            repositoryRoot,
            mcpPath,
            mcpWorkerPath,
            ["--workspace", repositoryRoot],
            TestContext.CancellationToken).ConfigureAwait(false);
        await AssertStartupArgumentRejectedAsync(
            repositoryRoot,
            mcpPath,
            mcpWorkerPath,
            ["--session", "1"],
            TestContext.CancellationToken).ConfigureAwait(false);
        await AssertStartupArgumentRejectedAsync(
            repositoryRoot,
            mcpPath,
            mcpWorkerPath,
            ["--socket", Path.Join(Path.GetTempPath(), "csls-unused.socket")],
            TestContext.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rejects every malformed tool and resource selector while preserving later valid requests.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task TargetToolsAndResourcesRejectEveryInvalidSelector()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        (string serverWorkerPath, string mcpPath, string mcpWorkerPath) =
            ResolveProductPaths(repositoryRoot);
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-mcp-invalid-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        int ownedProcessId = 0;
        ProcessExitObservation? ownedExit = null;
        try
        {
            string projectPath = Path.Join(fixturePath, "InvalidTarget.csproj");
            string documentPath = Path.Join(fixturePath, "Program.cs");
            await WriteWorkspaceAsync(
                projectPath,
                documentPath,
                "Console.WriteLine(Missing);",
                TestContext.CancellationToken).ConfigureAwait(false);
            string nonexistentWorkspace = Path.Join(fixturePath, "missing-workspace");
            string unavailableSocket = Path.Join(fixturePath, "missing-session.socket");

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
                await AssertToolErrorAsync(
                    client,
                    arguments: null,
                    "Specify exactly one target",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertToolErrorAsync(
                    client,
                    new Dictionary<string, object?>
                    {
                        ["workspace"] = projectPath,
                        ["session"] = int.MaxValue
                    },
                    "Specify exactly one target",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertToolErrorAsync(
                    client,
                    new Dictionary<string, object?>
                    {
                        ["workspace"] = projectPath,
                        ["session"] = int.MaxValue,
                        ["socket"] = unavailableSocket
                    },
                    "Specify exactly one target",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertToolErrorAsync(
                    client,
                    new Dictionary<string, object?> { ["workspace"] = " " },
                    "workspace must contain",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertToolErrorAsync(
                    client,
                    new Dictionary<string, object?> { ["workspace"] = nonexistentWorkspace },
                    "workspace does not exist",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertToolErrorAsync(
                    client,
                    new Dictionary<string, object?> { ["session"] = 0 },
                    "session must be a positive process identifier",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertToolErrorAsync(
                    client,
                    new Dictionary<string, object?> { ["session"] = -1 },
                    "session must be a positive process identifier",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertToolErrorAsync(
                    client,
                    new Dictionary<string, object?> { ["session"] = int.MaxValue },
                    "target could not be selected",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertToolErrorAsync(
                    client,
                    new Dictionary<string, object?> { ["socket"] = " " },
                    "socket must contain",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertToolErrorAsync(
                    client,
                    new Dictionary<string, object?> { ["socket"] = "relative.socket" },
                    "socket must be an absolute path",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertToolErrorAsync(
                    client,
                    new Dictionary<string, object?> { ["socket"] = unavailableSocket },
                    "target could not be selected",
                    TestContext.CancellationToken).ConfigureAwait(false);

                foreach (string uriTemplate in s_expectedResourceTemplates)
                {
                    bool requiresPath = uriTemplate.Contains(
                        ",path}",
                        StringComparison.Ordinal);
                    string resourcePath = uriTemplate.StartsWith(
                        "csls://project",
                        StringComparison.Ordinal)
                        ? projectPath
                        : documentPath;
                    var missingSelectorVariables = new Dictionary<string, object?>();
                    if (requiresPath)
                    {
                        missingSelectorVariables["path"] = resourcePath;
                    }

                    await AssertResourceErrorAsync(
                        client,
                        uriTemplate,
                        missingSelectorVariables,
                        "Specify exactly one target",
                        TestContext.CancellationToken).ConfigureAwait(false);

                    var multipleSelectorVariables = new Dictionary<string, object?>
                    {
                        ["workspace"] = projectPath,
                        ["session"] = int.MaxValue.ToString(CultureInfo.InvariantCulture)
                    };
                    if (requiresPath)
                    {
                        multipleSelectorVariables["path"] = resourcePath;
                    }

                    await AssertResourceErrorAsync(
                        client,
                        uriTemplate,
                        multipleSelectorVariables,
                        "Specify exactly one target",
                        TestContext.CancellationToken).ConfigureAwait(false);
                }

                await AssertResourceErrorAsync(
                    client,
                    "csls://session/{?workspace,session,socket}",
                    new Dictionary<string, object?>
                    {
                        ["workspace"] = projectPath,
                        ["session"] = int.MaxValue.ToString(CultureInfo.InvariantCulture),
                        ["socket"] = unavailableSocket
                    },
                    "Specify exactly one target",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertResourceErrorAsync(
                    client,
                    "csls://session/{?workspace,session,socket}",
                    new Dictionary<string, object?> { ["workspace"] = " " },
                    "workspace must contain",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertResourceErrorAsync(
                    client,
                    "csls://session/{?workspace,session,socket}",
                    new Dictionary<string, object?> { ["workspace"] = nonexistentWorkspace },
                    "workspace does not exist",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertResourceErrorAsync(
                    client,
                    "csls://session/{?workspace,session,socket}",
                    new Dictionary<string, object?> { ["session"] = string.Empty },
                    "session must be a positive process identifier",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertResourceErrorAsync(
                    client,
                    "csls://session/{?workspace,session,socket}",
                    new Dictionary<string, object?> { ["session"] = "-1" },
                    "session must be a positive process identifier",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertResourceErrorAsync(
                    client,
                    "csls://session/{?workspace,session,socket}",
                    new Dictionary<string, object?> { ["session"] = "not-a-process" },
                    "session must be a positive process identifier",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertResourceErrorAsync(
                    client,
                    "csls://session/{?workspace,session,socket}",
                    new Dictionary<string, object?>
                    {
                        ["session"] = int.MaxValue.ToString(CultureInfo.InvariantCulture)
                    },
                    "target could not be selected",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertResourceErrorAsync(
                    client,
                    "csls://session/{?workspace,session,socket}",
                    new Dictionary<string, object?> { ["socket"] = " " },
                    "socket must contain",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertResourceErrorAsync(
                    client,
                    "csls://session/{?workspace,session,socket}",
                    new Dictionary<string, object?> { ["socket"] = "relative.socket" },
                    "socket must be an absolute path",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertResourceErrorAsync(
                    client,
                    "csls://session/{?workspace,session,socket}",
                    new Dictionary<string, object?> { ["socket"] = unavailableSocket },
                    "target could not be selected",
                    TestContext.CancellationToken).ConfigureAwait(false);
                foreach (string uriTemplate in s_expectedResourceTemplates.Where(
                    static template => template.Contains(
                        ",path}",
                        StringComparison.Ordinal)))
                {
                    await AssertResourceErrorAsync(
                        client,
                        uriTemplate,
                        new Dictionary<string, object?> { ["workspace"] = projectPath },
                        "path must contain",
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await AssertResourceErrorAsync(
                        client,
                        uriTemplate,
                        new Dictionary<string, object?>
                        {
                            ["workspace"] = projectPath,
                            ["path"] = " "
                        },
                        "path must contain",
                        TestContext.CancellationToken).ConfigureAwait(false);
                }

                ReadResourceResult sessionResource = await client.ReadResourceAsync(
                    "csls://session/{?workspace,session,socket}",
                    new Dictionary<string, object?> { ["workspace"] = projectPath },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                ControlSessionInfo session = JsonSerializer.Deserialize(
                    GetResourceText(sessionResource),
                    ControlJsonSerializerContext.Default.ControlSessionInfo)
                    ?? throw new InvalidDataException(
                        "MCP returned no valid session after selector failures.");
                ownedProcessId = session.ProcessId;
                ownedExit = ProcessExitWaiter.Observe(ownedProcessId);
                Assert.AreEqual(projectPath, Assert.ContainsSingle(session.WorkspaceRoots));

                await AssertResourceErrorAsync(
                    client,
                    "csls://project/{?workspace,session,socket,path}",
                    new Dictionary<string, object?>
                    {
                        ["workspace"] = projectPath,
                        ["path"] = Path.Join(fixturePath, "Missing.csproj")
                    },
                    "No loaded csls project",
                    TestContext.CancellationToken).ConfigureAwait(false);

                ReadResourceResult workspaceResource = await client.ReadResourceAsync(
                    "csls://workspace/{?workspace,session,socket}",
                    new Dictionary<string, object?> { ["workspace"] = projectPath },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                ControlDashboardSnapshot workspace = JsonSerializer.Deserialize(
                    GetResourceText(workspaceResource),
                    ControlJsonSerializerContext.Default.ControlDashboardSnapshot)
                    ?? throw new InvalidDataException("MCP returned no valid workspace resource.");
                Assert.AreEqual(ownedProcessId, workspace.Session.ProcessId);
                Assert.Contains(
                    projectPath,
                    workspace.Projects.Select(static project => project.FilePath));

                ReadResourceResult projectResource = await client.ReadResourceAsync(
                    "csls://project/{?workspace,session,socket,path}",
                    new Dictionary<string, object?>
                    {
                        ["workspace"] = projectPath,
                        ["path"] = projectPath
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                ControlProjectInfo project = JsonSerializer.Deserialize(
                    GetResourceText(projectResource),
                    ControlJsonSerializerContext.Default.ControlProjectInfo)
                    ?? throw new InvalidDataException("MCP returned no valid project resource.");
                Assert.AreEqual(projectPath, project.FilePath);

                ReadResourceResult documentResource = await client.ReadResourceAsync(
                    "csls://document/{?workspace,session,socket,path}",
                    new Dictionary<string, object?>
                    {
                        ["workspace"] = projectPath,
                        ["path"] = documentPath
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                ControlDocumentInfo document = JsonSerializer.Deserialize(
                    GetResourceText(documentResource),
                    ControlJsonSerializerContext.Default.ControlDocumentInfo)
                    ?? throw new InvalidDataException("MCP returned no valid document resource.");
                Assert.AreEqual(documentPath, document.FilePath);

                ReadResourceResult diagnosticResource = await client.ReadResourceAsync(
                    "csls://diagnostic/{?workspace,session,socket,path}",
                    new Dictionary<string, object?>
                    {
                        ["workspace"] = projectPath,
                        ["path"] = documentPath
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                DocumentDiagnosticReport diagnostics = JsonSerializer.Deserialize(
                    GetResourceText(diagnosticResource),
                    ControlJsonSerializerContext.Default.DocumentDiagnosticReport)
                    ?? throw new InvalidDataException("MCP returned no valid diagnostic resource.");
                Assert.Contains(
                    "CS0103",
                    diagnostics.Items?.Select(static diagnostic => diagnostic.Code) ?? []);
            }
            finally
            {
                await DisconnectMcpAsync(
                    mcp,
                    TestContext.CancellationToken).ConfigureAwait(false);
            }

            await ProcessExitWaiter.WaitAsync(
                ownedExit ?? throw new InvalidOperationException(
                    "The valid workspace request did not create a transient process."),
                TimeSpan.FromSeconds(10),
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertFileDeletedAsync(
                ControlEndpoint.GetSocketPath(ownedProcessId),
                TimeSpan.FromSeconds(10),
                TestContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (ownedExit is ProcessExitObservation exit)
            {
                await ProcessExitWaiter.WaitAsync(
                    exit,
                    TimeSpan.FromSeconds(10),
                    TestContext.CancellationToken).ConfigureAwait(false);
            }

            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
