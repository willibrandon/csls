using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies target selection, failure isolation, cancellation, and ownership through real MCP processes.
/// </summary>
[TestClass]
public sealed class McpWorkspaceSelectionTests
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

            foreach (string toolName in expectedTargetTools)
            {
                McpClientTool tool = Assert.ContainsSingle(
                    tools.Where(candidate => candidate.Name == toolName));
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
                Assert.IsTrue(sharedResult.StructuredContent.HasValue);
                JsonElement workspaceSummary = sharedResult.StructuredContent.Value;
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

    private static (string ServerWorkerPath, string McpPath, string McpWorkerPath)
        ResolveProductPaths(string repositoryRoot)
    {
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
        Assert.IsTrue(
            File.Exists(serverWorkerPath),
            $"Server worker not found at {serverWorkerPath}.");
        Assert.IsTrue(File.Exists(mcpPath), $"MCP launcher not found at {mcpPath}.");
        Assert.IsTrue(
            File.Exists(mcpWorkerPath),
            $"MCP worker not found at {mcpWorkerPath}.");
        return (serverWorkerPath, mcpPath, mcpWorkerPath);
    }

    private async Task<McpClient> CreateMcpClientAsync(
        string repositoryRoot,
        string mcpPath,
        string mcpWorkerPath,
        string name,
        string? serverWorkerPath,
        CancellationToken cancellationToken)
    {
        string dotnetHost = EditorToolResolver.ResolveAbsoluteDotNetHost();
        Dictionary<string, string?> environment =
            StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["DOTNET_ROOT"] = EditorToolResolver.ResolveDotNetRoot();
        environment["DOTNET_HOST_PATH"] = dotnetHost;
        environment["CSLS_MCP_WORKER_PATH"] = mcpWorkerPath;
        if (serverWorkerPath is not null)
        {
            environment["CSLS_SERVER_WORKER_PATH"] = serverWorkerPath;
        }

        bool isManagedLauncher = string.Equals(
            Path.GetExtension(mcpPath),
            ".dll",
            StringComparison.OrdinalIgnoreCase);
        List<string> arguments = [];
        if (isManagedLauncher)
        {
            arguments.Add(mcpPath);
        }

        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Command = isManagedLauncher ? dotnetHost : mcpPath,
                Arguments = arguments,
                Name = name,
                WorkingDirectory = repositoryRoot,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = environment,
                StandardErrorLines = TestContext.WriteLine
            });
        return await McpClient.CreateAsync(
            transport,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<LspProcessSession> StartLanguageServerAsync(
        string repositoryRoot,
        string serverWorkerPath,
        string workspacePath,
        string name,
        CancellationToken cancellationToken)
    {
        LspProcessSession lsp = await LspProcessSession.StartAsync(
            name,
            EditorToolResolver.ResolveDotNetHost(),
            [serverWorkerPath],
            repositoryRoot).ConfigureAwait(false);
        try
        {
            await lsp.InitializeAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            await lsp.CompleteInitializationAsync().ConfigureAwait(false);
            await ControlSessionWaiter.WaitForRunningAsync(
                workspacePath,
                TimeSpan.FromSeconds(60),
                cancellationToken).ConfigureAwait(false);
            return lsp;
        }
        catch
        {
            await lsp.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DisconnectMcpAsync(
        McpProcessSession mcp,
        CancellationToken cancellationToken)
    {
        string diagnostics = await mcp.DisconnectAsync(
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        Assert.DoesNotContain(
            "Unhandled exception",
            diagnostics,
            StringComparison.Ordinal);
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

    private static async Task AssertToolErrorAsync(
        McpClient client,
        Dictionary<string, object?>? arguments,
        string expectedMessage,
        CancellationToken cancellationToken)
    {
        CallToolResult result = arguments is null
            ? await client.CallToolAsync(
                "get_session",
                cancellationToken: cancellationToken).ConfigureAwait(false)
            : await client.CallToolAsync(
                "get_session",
                arguments,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(result.IsError);
        Assert.IsNull(result.StructuredContent);
        Assert.Contains(
            expectedMessage,
            result.Content.OfType<TextContentBlock>().Single().Text,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertStartupArgumentRejectedAsync(
        string repositoryRoot,
        string mcpPath,
        string mcpWorkerPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        string dotnetHost = EditorToolResolver.ResolveAbsoluteDotNetHost();
        bool isManagedLauncher = string.Equals(
            Path.GetExtension(mcpPath),
            ".dll",
            StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isManagedLauncher ? dotnetHost : mcpPath,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot
        };
        if (isManagedLauncher)
        {
            startInfo.ArgumentList.Add(mcpPath);
        }

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_ROOT"] = EditorToolResolver.ResolveDotNetRoot();
        startInfo.Environment["DOTNET_HOST_PATH"] = dotnetHost;
        startInfo.Environment["CSLS_MCP_WORKER_PATH"] = mcpWorkerPath;
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The production MCP launcher did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        string diagnostics = string.Concat(
            await outputTask.ConfigureAwait(false),
            Environment.NewLine,
            await errorTask.ConfigureAwait(false));
        Assert.AreNotEqual(0, process.ExitCode, diagnostics);
        Assert.Contains(arguments[0], diagnostics, StringComparison.Ordinal);
    }

    private static void AssertToolAnnotations(
        McpClientTool tool,
        bool readOnly,
        bool destructive,
        bool idempotent,
        bool openWorld)
    {
        ToolAnnotations annotations = tool.ProtocolTool.Annotations
            ?? throw new InvalidDataException(
                $"Tool {tool.Name} published no MCP behavior annotations.");
        Assert.AreEqual(readOnly, annotations.ReadOnlyHint, tool.Name);
        Assert.AreEqual(destructive, annotations.DestructiveHint, tool.Name);
        Assert.AreEqual(idempotent, annotations.IdempotentHint, tool.Name);
        Assert.AreEqual(openWorld, annotations.OpenWorldHint, tool.Name);
    }

    private static async Task AssertResourceErrorAsync(
        McpClient client,
        string uriTemplate,
        Dictionary<string, object?> variables,
        string expectedMessage,
        CancellationToken cancellationToken)
    {
        McpProtocolException exception = await Assert.ThrowsExactlyAsync<McpProtocolException>(
            async () => await client.ReadResourceAsync(
                uriTemplate,
                variables,
                cancellationToken: cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.Contains(
            expectedMessage,
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ControlSessionInfo> CallSessionAsync(
        McpClient client,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        CallToolResult result = await client.CallToolAsync(
            "get_session",
            arguments,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Assert.IsNull(result.IsError);
        Assert.IsTrue(result.StructuredContent.HasValue);
        return result.StructuredContent.Value.Deserialize(
            ControlJsonSerializerContext.Default.ControlSessionInfo)
            ?? throw new InvalidDataException("MCP returned no selected session.");
    }

    private static string GetResourceText(ReadResourceResult result) =>
        result.Contents.OfType<TextResourceContents>().Single().Text;

    private static async Task AssertFileDeletedAsync(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            while (File.Exists(path))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeoutSource.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"File was not deleted within {timeout}: {path}");
        }

        Assert.IsFalse(File.Exists(path));
    }

    private static string GetSchemaType(JsonElement schema)
    {
        if (schema.TryGetProperty("type", out JsonElement type))
        {
            return type.ValueKind == JsonValueKind.String
                ? type.GetString() ?? string.Empty
                : type
                    .EnumerateArray()
                    .Select(static item => item.GetString())
                    .Single(static typeName => typeName != "null") ?? string.Empty;
        }

        return schema
            .GetProperty("anyOf")
            .EnumerateArray()
            .Select(static option => option.GetProperty("type").GetString())
            .Single(static typeName => typeName != "null") ?? string.Empty;
    }

    private static string CreateBlockedProjectText(
        string processHostPath,
        string buildStartedPath,
        string buildReleasePath)
    {
        string dotnetPath = EditorToolResolver.ResolveDotNetHost();
        string escapedDotnetPath = SecurityElement.Escape(dotnetPath)
            ?? throw new InvalidOperationException("The dotnet path could not be escaped.");
        string escapedProcessHostPath = SecurityElement.Escape(processHostPath)
            ?? throw new InvalidOperationException("The process-host path could not be escaped.");
        string escapedBuildStartedPath = SecurityElement.Escape(buildStartedPath)
            ?? throw new InvalidOperationException("The build marker path could not be escaped.");
        string escapedBuildReleasePath = SecurityElement.Escape(buildReleasePath)
            ?? throw new InvalidOperationException("The build release path could not be escaped.");
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <Target Name="BlockDesignTimeBuild"
                      BeforeTargets="Compile"
                      Condition="'$(DesignTimeBuild)' == 'true'">
                <WriteLinesToFile File="{{escapedBuildStartedPath}}"
                                  Lines="started"
                                  Overwrite="true" />
                <Exec Command="&quot;{{escapedDotnetPath}}&quot; &quot;{{escapedProcessHostPath}}&quot; --wait-for-file &quot;{{escapedBuildReleasePath}}&quot;" />
              </Target>
            </Project>
            """;
    }
}
