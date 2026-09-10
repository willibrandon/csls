using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies the official MCP C# SDK against a real csls worker and Unix-domain socket.
/// </summary>
[TestClass]
public sealed partial class McpLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Lists and invokes real tools, resources, and prompts through MCP standard input and output.
    /// </summary>
    [TestMethod]
    public async Task McpExposesAttachedLanguageServerSession()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string workerPath = Path.Join(
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
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        Assert.IsTrue(File.Exists(mcpPath), $"MCP launcher not found at {mcpPath}.");
        Assert.IsTrue(
            File.Exists(mcpWorkerPath),
            $"MCP worker not found at {mcpWorkerPath}.");

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string projectPath = Path.Join(fixturePath, "Fixture.csproj");
            string documentPath = Path.Join(fixturePath, "Program.cs");
            string importsPath = Path.Join(fixturePath, "Imports.cs");
            string missingUsingPath = Path.Join(fixturePath, "MissingUsing.cs");
            string implementInterfacePath = Path.Join(fixturePath, "ImplementInterface.cs");
            string formattingPath = Path.Join(fixturePath, "Formatting.cs");
            string stalePath = Path.Join(fixturePath, "Stale.cs");
            string advancedPath = Path.Join(fixturePath, "Advanced.cs");
            string moveTypePath = Path.Join(fixturePath, "MoveTypes.cs");
            string movedTypePath = Path.Join(fixturePath, "McpHelper.cs");
            await File.WriteAllTextAsync(
                projectPath,
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                importsPath,
                ImportsText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                missingUsingPath,
                MissingUsingText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                implementInterfacePath,
                ImplementInterfaceText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                formattingPath,
                FormattingText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                stalePath,
                FormattingText.Replace("Formatting", "Stale", StringComparison.Ordinal),
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                advancedPath,
                AdvancedDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                moveTypePath,
                MoveTypeDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-mcp-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                repositoryRoot).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "csls",
                initialization.GetProperty("serverInfo").GetProperty("name").GetString());
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);
            await ControlSessionWaiter.WaitForRunningAsync(
                fixturePath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);

            string dotnetHost = EditorToolResolver.ResolveAbsoluteDotNetHost();
            Dictionary<string, string?> environment =
                StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            environment["DOTNET_ROOT"] = EditorToolResolver.ResolveDotNetRoot();
            environment["CSLS_MCP_WORKER_PATH"] = mcpWorkerPath;
            bool isManagedLauncher = string.Equals(
                Path.GetExtension(mcpPath),
                ".dll",
                StringComparison.OrdinalIgnoreCase);
            List<string> mcpArguments = [];
            if (isManagedLauncher)
            {
                mcpArguments.Add(mcpPath);
            }

            var transport = new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Command = isManagedLauncher ? dotnetHost : mcpPath,
                    Arguments = mcpArguments,
                    Name = "csls-mcp-integration",
                    WorkingDirectory = repositoryRoot,
                    InheritEnvironmentVariables = false,
                    EnvironmentVariables = environment,
                    StandardErrorLines = TestContext.WriteLine
                });
            McpClient client = await McpClient.CreateAsync(
                transport,
                cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
            try
            {
                Assert.AreEqual("csls", client.ServerInfo.Name);
                string negotiatedProtocolVersion = client.NegotiatedProtocolVersion
                    ?? throw new InvalidDataException(
                        "The MCP client did not negotiate a protocol version.");

                IList<McpClientTool> tools = await client
                    .ListToolsAsync(cancellationToken: TestContext.CancellationToken)
                    .ConfigureAwait(false);
                McpClientTool sessionTool = tools.Single(static tool =>
                    tool.Name == "get_session");
                McpClientTool sessionsTool = tools.Single(static tool =>
                    tool.Name == "list_sessions");
                McpClientTool workspaceStateTool = tools.Single(static tool =>
                    tool.Name == "get_workspace_state");
                McpClientTool restoreWorkspaceTool = tools.Single(static tool =>
                    tool.Name == "restore_workspace");
                McpClientTool reloadWorkspaceTool = tools.Single(static tool =>
                    tool.Name == "reload_workspace");
                McpClientTool restartBuildHostsTool = tools.Single(static tool =>
                    tool.Name == "restart_build_hosts");
                McpClientTool clearCachesTool = tools.Single(static tool =>
                    tool.Name == "clear_caches");
                McpClientTool hoverTool = tools.Single(static tool =>
                    tool.Name == "get_hover");
                McpClientTool diagnosticTool = tools.Single(static tool =>
                    tool.Name == "get_diagnostics");
                McpClientTool completionTool = tools.Single(static tool =>
                    tool.Name == "get_completion");
                McpClientTool definitionTool = tools.Single(static tool =>
                    tool.Name == "get_definition");
                McpClientTool declarationTool = tools.Single(static tool =>
                    tool.Name == "get_declaration");
                McpClientTool typeDefinitionTool = tools.Single(static tool =>
                    tool.Name == "get_type_definition");
                McpClientTool implementationTool = tools.Single(static tool =>
                    tool.Name == "get_implementation");
                McpClientTool selectionRangeTool = tools.Single(static tool =>
                    tool.Name == "get_selection_range");
                McpClientTool documentHighlightsTool = tools.Single(static tool =>
                    tool.Name == "get_document_highlights");
                McpClientTool referencesTool = tools.Single(static tool =>
                    tool.Name == "get_references");
                McpClientTool documentSymbolsTool = tools.Single(static tool =>
                    tool.Name == "get_document_symbols");
                McpClientTool workspaceSymbolsTool = tools.Single(static tool =>
                    tool.Name == "search_workspace_symbols");
                McpClientTool signatureHelpTool = tools.Single(static tool =>
                    tool.Name == "get_signature_help");
                McpClientTool renameTool = tools.Single(static tool =>
                    tool.Name == "preview_rename");
                McpClientTool formattingTool = tools.Single(static tool =>
                    tool.Name == "preview_formatting");
                McpClientTool codeActionsTool = tools.Single(static tool =>
                    tool.Name == "get_code_actions");
                McpClientTool applyEditPlanTool = tools.Single(static tool =>
                    tool.Name == "apply_edit_plan");
                ToolAnnotations annotations = sessionTool.ProtocolTool.Annotations
                    ?? throw new InvalidDataException("The session tool has no MCP annotations.");
                Assert.IsTrue(annotations.ReadOnlyHint);
                Assert.IsFalse(annotations.DestructiveHint);
                Assert.IsFalse(annotations.OpenWorldHint);
                Assert.IsTrue(annotations.IdempotentHint);
                Assert.IsNotNull(sessionTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(sessionsTool.ProtocolTool.OutputSchema);
                foreach (McpClientTool targetTool in tools.Where(static tool =>
                    tool.Name != "list_sessions"))
                {
                    JsonElement inputProperties = targetTool.ProtocolTool.InputSchema
                        .GetProperty("properties");
                    Assert.IsTrue(
                        inputProperties.TryGetProperty("workspace", out _),
                        $"Tool {targetTool.Name} omitted the workspace selector.");
                    Assert.IsTrue(
                        inputProperties.TryGetProperty("session", out _),
                        $"Tool {targetTool.Name} omitted the session selector.");
                    Assert.IsTrue(
                        inputProperties.TryGetProperty("socket", out _),
                        $"Tool {targetTool.Name} omitted the socket selector.");
                }

                JsonElement sessionsInputProperties = sessionsTool.ProtocolTool.InputSchema
                    .GetProperty("properties");
                Assert.IsFalse(sessionsInputProperties.TryGetProperty("workspace", out _));
                Assert.IsFalse(sessionsInputProperties.TryGetProperty("session", out _));
                Assert.IsFalse(sessionsInputProperties.TryGetProperty("socket", out _));
                Assert.IsNotNull(workspaceStateTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(restoreWorkspaceTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(reloadWorkspaceTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(restartBuildHostsTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(clearCachesTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(hoverTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(diagnosticTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(completionTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(definitionTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(declarationTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(typeDefinitionTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(implementationTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(selectionRangeTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(documentHighlightsTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(referencesTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(documentSymbolsTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(workspaceSymbolsTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(signatureHelpTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(renameTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(formattingTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(codeActionsTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(applyEditPlanTool.ProtocolTool.OutputSchema);
                ToolAnnotations applyAnnotations = applyEditPlanTool.ProtocolTool.Annotations
                    ?? throw new InvalidDataException(
                        "The apply edit plan tool has no MCP annotations.");
                Assert.IsFalse(applyAnnotations.ReadOnlyHint);
                Assert.IsTrue(applyAnnotations.DestructiveHint);
                Assert.IsFalse(applyAnnotations.IdempotentHint);
                ToolAnnotations workspaceAnnotations = workspaceStateTool.ProtocolTool.Annotations
                    ?? throw new InvalidDataException(
                        "The workspace state tool has no MCP annotations.");
                Assert.IsTrue(workspaceAnnotations.ReadOnlyHint);
                Assert.IsFalse(workspaceAnnotations.DestructiveHint);
                ToolAnnotations restoreAnnotations = restoreWorkspaceTool.ProtocolTool.Annotations
                    ?? throw new InvalidDataException(
                        "The restore workspace tool has no MCP annotations.");
                Assert.IsFalse(restoreAnnotations.ReadOnlyHint);
                Assert.IsFalse(restoreAnnotations.DestructiveHint);
                ToolAnnotations clearAnnotations = clearCachesTool.ProtocolTool.Annotations
                    ?? throw new InvalidDataException(
                        "The clear caches tool has no MCP annotations.");
                Assert.IsFalse(clearAnnotations.ReadOnlyHint);
                Assert.IsTrue(clearAnnotations.DestructiveHint);

                CallToolResult sessionResult = await client.CallToolAsync(
                    "get_session",
                    new Dictionary<string, object?>
                    {
                        ["socket"] = ControlEndpoint.GetSocketPath(lsp.ProcessId)
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(sessionResult.IsError);
                ControlSessionInfo session = McpAssertions.GetStructuredContent(sessionResult).Deserialize(
                    ControlJsonSerializerContext.Default.ControlSessionInfo)
                    ?? throw new InvalidDataException("MCP returned no structured session value.");
                Assert.AreEqual(lsp.ProcessId, session.ProcessId);
                Assert.AreEqual("Running", session.LifecycleState);
                Assert.AreEqual(fixturePath, session.WorkspaceRoots.Single());

                CallToolResult workspaceStateResult = await client.CallToolAsync(
                    "get_workspace_state",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(workspaceStateResult.IsError);
                JsonElement workspaceState = McpAssertions.GetStructuredContent(workspaceStateResult);
                Assert.AreEqual(
                    lsp.ProcessId,
                    workspaceState.GetProperty("processId").GetInt32());
                Assert.AreEqual(1, workspaceState.GetProperty("projectCount").GetInt32());
                Assert.IsGreaterThanOrEqualTo(
                    1,
                    workspaceState.GetProperty("documentCount").GetInt32());
                Assert.AreEqual(
                    $"csls://workspace/?session={lsp.ProcessId}",
                    workspaceState.GetProperty("detailsUri").GetString());

                CallToolResult hoverResult = await client.CallToolAsync(
                    "get_hover",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = documentPath,
                        ["line"] = 6,
                        ["character"] = 10
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(hoverResult.IsError);
                ControlHoverResult hover = McpAssertions.GetStructuredContent(hoverResult).Deserialize(
                    ControlJsonSerializerContext.Default.ControlHoverResult)
                    ?? throw new InvalidDataException("MCP returned no structured hover value.");
                Assert.IsTrue(hover.Found);
                Assert.IsNotNull(hover.Hover);
                Assert.Contains("System.Console", hover.Hover.Contents.Value);

                CallToolResult diagnosticResult = await client.CallToolAsync(
                    "get_diagnostics",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = documentPath
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(diagnosticResult.IsError);
                DocumentDiagnosticReport diagnosticReport =
                    McpAssertions.GetStructuredContent(diagnosticResult).Deserialize(
                        ControlJsonSerializerContext.Default.DocumentDiagnosticReport)
                    ?? throw new InvalidDataException(
                        "MCP returned no structured diagnostic report.");
                Assert.AreEqual("full", diagnosticReport.Kind);
                IReadOnlyList<Diagnostic> diagnosticItems = diagnosticReport.Items
                    ?? throw new InvalidDataException(
                        "MCP returned a full diagnostic report without items.");
                Assert.Contains(
                    "CS0103",
                    diagnosticItems.Select(static diagnostic => diagnostic.Code));

                CallToolResult completionResult = await client.CallToolAsync(
                    "get_completion",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = documentPath,
                        ["line"] = 6,
                        ["character"] = 19
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(completionResult.IsError);
                CompletionList completion = McpAssertions.GetStructuredContent(completionResult).Deserialize(
                    ControlJsonSerializerContext.Default.CompletionList)
                    ?? throw new InvalidDataException(
                        "MCP returned no structured completion list.");
                Assert.Contains(
                    "WriteLine",
                    completion.Items.Select(static item => item.Label));

                CallToolResult definitionResult = await client.CallToolAsync(
                    "get_definition",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = documentPath,
                        ["line"] = 7,
                        ["character"] = 9
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                IReadOnlyList<Location> definitions =
                    GetStructuredCollection(
                        definitionResult,
                        negotiatedProtocolVersion).Deserialize(
                        ControlJsonSerializerContext.Default.IReadOnlyListLocation)
                    ?? throw new InvalidDataException(
                        "MCP returned no structured definition locations.");
                Location definition = Assert.ContainsSingle(definitions);
                Assert.AreEqual(new Position(10, 24), definition.Range.Start);

                CallToolResult declarationResult = await client.CallToolAsync(
                    "get_declaration",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = advancedPath,
                        ["line"] = 19,
                        ["character"] = 17
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Location declaration = Assert.ContainsSingle(
                    GetStructuredCollection(
                        declarationResult,
                        negotiatedProtocolVersion).Deserialize(
                        ControlJsonSerializerContext.Default.IReadOnlyListLocation)
                    ?? throw new InvalidDataException("MCP returned no declaration locations."));
                Assert.AreEqual(new Position(4, 9), declaration.Range.Start);

                CallToolResult typeDefinitionResult = await client.CallToolAsync(
                    "get_type_definition",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = advancedPath,
                        ["line"] = 18,
                        ["character"] = 17
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Location typeDefinition = Assert.ContainsSingle(
                    GetStructuredCollection(
                        typeDefinitionResult,
                        negotiatedProtocolVersion).Deserialize(
                        ControlJsonSerializerContext.Default.IReadOnlyListLocation)
                    ?? throw new InvalidDataException(
                        "MCP returned no type-definition locations."));
                Assert.AreEqual(new Position(2, 17), typeDefinition.Range.Start);

                CallToolResult implementationResult = await client.CallToolAsync(
                    "get_implementation",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = advancedPath,
                        ["line"] = 4,
                        ["character"] = 10
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Location implementation = Assert.ContainsSingle(
                    GetStructuredCollection(
                        implementationResult,
                        negotiatedProtocolVersion).Deserialize(
                        ControlJsonSerializerContext.Default.IReadOnlyListLocation)
                    ?? throw new InvalidDataException(
                        "MCP returned no implementation locations."));
                Assert.AreEqual(new Position(9, 16), implementation.Range.Start);

                CallToolResult selectionRangeResult = await client.CallToolAsync(
                    "get_selection_range",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = advancedPath,
                        ["line"] = 19,
                        ["character"] = 17
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(selectionRangeResult.IsError);
                SelectionRange selectionRange = McpAssertions.GetStructuredContent(selectionRangeResult)
                    .Deserialize(ControlJsonSerializerContext.Default.SelectionRange)
                    ?? throw new InvalidDataException("MCP returned no selection range.");
                Assert.AreEqual(new Position(19, 15), selectionRange.Range.Start);
                Assert.IsNotNull(selectionRange.Parent);

                CallToolResult highlightsResult = await client.CallToolAsync(
                    "get_document_highlights",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = advancedPath,
                        ["line"] = 18,
                        ["character"] = 17
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                IReadOnlyList<DocumentHighlight> highlights = GetStructuredCollection(
                    highlightsResult,
                    negotiatedProtocolVersion).Deserialize(
                        ControlJsonSerializerContext.Default.IReadOnlyListDocumentHighlight)
                    ?? throw new InvalidDataException("MCP returned no document highlights.");
                Assert.HasCount(4, highlights);
                Assert.AreEqual(
                    DocumentHighlightKind.Write,
                    highlights.Single(static highlight =>
                        highlight.Range.Start == new Position(20, 8)).Kind);

                CallToolResult referencesResult = await client.CallToolAsync(
                    "get_references",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = documentPath,
                        ["line"] = 7,
                        ["character"] = 9,
                        ["includeDeclaration"] = false
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                IReadOnlyList<Location> references =
                    GetStructuredCollection(
                        referencesResult,
                        negotiatedProtocolVersion).Deserialize(
                        ControlJsonSerializerContext.Default.IReadOnlyListLocation)
                    ?? throw new InvalidDataException(
                        "MCP returned no structured reference locations.");
                Location reference = Assert.ContainsSingle(references);
                Assert.AreEqual(new Position(7, 8), reference.Range.Start);

                CallToolResult documentSymbolsResult = await client.CallToolAsync(
                    "get_document_symbols",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = documentPath
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                IReadOnlyList<DocumentSymbol> documentSymbols =
                    GetStructuredCollection(
                        documentSymbolsResult,
                        negotiatedProtocolVersion).Deserialize(
                        ControlJsonSerializerContext.Default.IReadOnlyListDocumentSymbol)
                    ?? throw new InvalidDataException(
                        "MCP returned no structured document symbols.");
                DocumentSymbol sourceNamespace = Assert.ContainsSingle(documentSymbols);
                Assert.AreEqual("Fixture", sourceNamespace.Name);
                Assert.IsNotNull(sourceNamespace.Children);
                Assert.Contains(
                    "Program",
                    sourceNamespace.Children.Select(static symbol => symbol.Name));

                CallToolResult workspaceSymbolsResult = await client.CallToolAsync(
                    "search_workspace_symbols",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["query"] = "Help"
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                IReadOnlyList<WorkspaceSymbol> workspaceSymbols =
                    GetStructuredCollection(
                        workspaceSymbolsResult,
                        negotiatedProtocolVersion).Deserialize(
                        ControlJsonSerializerContext.Default.IReadOnlyListWorkspaceSymbol)
                    ?? throw new InvalidDataException(
                        "MCP returned no structured workspace symbols.");
                WorkspaceSymbol helper = workspaceSymbols.Single(static symbol =>
                    symbol.Name == "Helper");
                LspRange helperRange = helper.Location.Range ?? throw new InvalidDataException(
                    "MCP returned an unresolved Helper workspace symbol.");
                Assert.AreEqual(new Position(10, 24), helperRange.Start);

                CallToolResult signatureHelpResult = await client.CallToolAsync(
                    "get_signature_help",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = documentPath,
                        ["line"] = 7,
                        ["character"] = 15
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(signatureHelpResult.IsError);
                SignatureHelp signatureHelp = McpAssertions.GetStructuredContent(signatureHelpResult).Deserialize(
                    ControlJsonSerializerContext.Default.SignatureHelp)
                    ?? throw new InvalidDataException(
                        "MCP returned no structured signature help.");
                SignatureInformation helperSignature = Assert.ContainsSingle(
                    signatureHelp.Signatures);
                Assert.Contains("Helper", helperSignature.Label, StringComparison.Ordinal);
                Assert.IsNotNull(helperSignature.Parameters);
                ParameterInformation helperParameter = Assert.ContainsSingle(
                    helperSignature.Parameters);
                Assert.AreEqual("int value", helperParameter.Label);

                CallToolResult renameResult = await client.CallToolAsync(
                    "preview_rename",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = documentPath,
                        ["line"] = 7,
                        ["character"] = 10,
                        ["newName"] = "RenamedHelper"
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(renameResult.IsError);
                ControlEditPlan rename = McpAssertions.GetStructuredContent(renameResult).Deserialize(
                    ControlJsonSerializerContext.Default.ControlEditPlan)
                    ?? throw new InvalidDataException("MCP returned no rename edit plan.");
                TextDocumentEdit renameDocument = Assert.ContainsSingle(
                    rename.Edit.DocumentChanges.OfType<TextDocumentEdit>());
                Assert.AreEqual(1, renameDocument.TextDocument.Version);
                Assert.HasCount(2, renameDocument.Edits);
                Assert.IsTrue(renameDocument.Edits.All(static edit =>
                    edit.NewText == "Renamed"));
                CallToolResult openApplyResult = await client.CallToolAsync(
                    "apply_edit_plan",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["planId"] = rename.PlanId.ToString("D")
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsTrue(openApplyResult.IsError);
                Assert.Contains(
                    "Helper(1)",
                    await File.ReadAllTextAsync(
                        documentPath,
                        TestContext.CancellationToken).ConfigureAwait(false),
                    StringComparison.Ordinal);

                CallToolResult formattingResult = await client.CallToolAsync(
                    "preview_formatting",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = formattingPath,
                        ["tabSize"] = 4,
                        ["insertSpaces"] = true
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(formattingResult.IsError);
                ControlEditPlan formatting = McpAssertions.GetStructuredContent(formattingResult).Deserialize(
                    ControlJsonSerializerContext.Default.ControlEditPlan)
                    ?? throw new InvalidDataException("MCP returned no formatting edit plan.");
                TextDocumentEdit formattingDocument = Assert.ContainsSingle(
                    formatting.Edit.DocumentChanges.OfType<TextDocumentEdit>());
                Assert.IsNull(formattingDocument.TextDocument.Version);
                Assert.IsNotEmpty(formattingDocument.Edits);

                CallToolResult applyResult = await client.CallToolAsync(
                    "apply_edit_plan",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["planId"] = formatting.PlanId.ToString("D")
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(applyResult.IsError);
                ControlApplyEditPlanResult applied = McpAssertions.GetStructuredContent(applyResult).Deserialize(
                    ControlJsonSerializerContext.Default.ControlApplyEditPlanResult)
                    ?? throw new InvalidDataException("MCP returned no applied edit result.");
                Assert.Contains(formattingPath, applied.DocumentPaths);
                string appliedFormatting = await File.ReadAllTextAsync(
                    formattingPath,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.Contains(
                    "Add(int left, int right) => left + right",
                    appliedFormatting,
                    StringComparison.Ordinal);
                CallToolResult duplicateApplyResult = await client.CallToolAsync(
                    "apply_edit_plan",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["planId"] = formatting.PlanId.ToString("D")
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsTrue(duplicateApplyResult.IsError);

                CallToolResult stalePreviewResult = await client.CallToolAsync(
                    "preview_formatting",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = stalePath,
                        ["tabSize"] = 4,
                        ["insertSpaces"] = true
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(stalePreviewResult.IsError);
                ControlEditPlan stalePlan = McpAssertions.GetStructuredContent(stalePreviewResult).Deserialize(
                    ControlJsonSerializerContext.Default.ControlEditPlan)
                    ?? throw new InvalidDataException("MCP returned no stale edit plan.");
                await File.AppendAllTextAsync(
                    stalePath,
                    $"{Environment.NewLine}// external change",
                    TestContext.CancellationToken).ConfigureAwait(false);
                CallToolResult staleApplyResult = await client.CallToolAsync(
                    "apply_edit_plan",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["planId"] = stalePlan.PlanId.ToString("D")
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsTrue(staleApplyResult.IsError);
                Assert.EndsWith(
                    "// external change",
                    await File.ReadAllTextAsync(
                        stalePath,
                        TestContext.CancellationToken).ConfigureAwait(false),
                    StringComparison.Ordinal);

                CallToolResult codeActionsResult = await client.CallToolAsync(
                    "get_code_actions",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = importsPath,
                        ["startLine"] = 0,
                        ["startCharacter"] = 0,
                        ["endLine"] = 1,
                        ["endCharacter"] = 13,
                        ["kind"] = "source"
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                IReadOnlyList<ControlCodeActionPlan> codeActions = GetStructuredCollection(
                    codeActionsResult,
                    negotiatedProtocolVersion)
                    .Deserialize(ControlJsonSerializerContext.Default.IReadOnlyListControlCodeActionPlan)
                    ?? throw new InvalidDataException("MCP returned no code action previews.");
                ControlCodeActionPlan organizeImports = Assert.ContainsSingle(codeActions);
                Assert.AreEqual("source.organizeImports", organizeImports.Action.Kind);
                Assert.IsNotNull(organizeImports.EditPlan);
                Assert.IsNotEmpty(organizeImports.EditPlan.Edit.DocumentChanges);

                CallToolResult quickFixResult = await client.CallToolAsync(
                    "get_code_actions",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = missingUsingPath,
                        ["startLine"] = 6,
                        ["startCharacter"] = 26,
                        ["endLine"] = 6,
                        ["endCharacter"] = 39,
                        ["kind"] = "quickfix"
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                IReadOnlyList<ControlCodeActionPlan> quickFixes = GetStructuredCollection(
                    quickFixResult,
                    negotiatedProtocolVersion)
                    .Deserialize(
                        ControlJsonSerializerContext.Default.IReadOnlyListControlCodeActionPlan)
                    ?? throw new InvalidDataException("MCP returned no quick-fix previews.");
                Assert.Contains(
                    "System.Text.StringBuilder",
                    quickFixes.Select(static candidate => candidate.Action.Title));
                ControlCodeActionPlan quickFix = Assert.ContainsSingle(quickFixes.Where(
                    static candidate => candidate.Action.Title == "using System.Text;"));
                Assert.AreEqual("using System.Text;", quickFix.Action.Title);
                Assert.AreEqual("quickfix", quickFix.Action.Kind);
                ControlEditPlan quickFixPlan = quickFix.EditPlan
                    ?? throw new InvalidDataException("MCP returned no quick-fix edit plan.");
                CallToolResult quickFixApplyResult = await client.CallToolAsync(
                    "apply_edit_plan",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["planId"] = quickFixPlan.PlanId.ToString("D")
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(quickFixApplyResult.IsError);
                string fixedMissingUsing = await File.ReadAllTextAsync(
                    missingUsingPath,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.StartsWith(
                    "using System.Text;",
                    fixedMissingUsing,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "new StringBuilder()",
                    fixedMissingUsing,
                    StringComparison.Ordinal);

                CallToolResult implementActionResult = await client.CallToolAsync(
                    "get_code_actions",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = implementInterfacePath,
                        ["startLine"] = 7,
                        ["startCharacter"] = 29,
                        ["endLine"] = 7,
                        ["endCharacter"] = 29,
                        ["kind"] = "quickfix"
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                IReadOnlyList<ControlCodeActionPlan> implementations =
                    GetStructuredCollection(implementActionResult, negotiatedProtocolVersion)
                        .Deserialize(
                            ControlJsonSerializerContext
                                .Default
                                .IReadOnlyListControlCodeActionPlan)
                    ?? throw new InvalidDataException(
                        "MCP returned no interface implementation previews.");
                ControlCodeActionPlan implementAction = Assert.ContainsSingle(
                    implementations.Where(static candidate =>
                        candidate.Action.Title == "Implement interface"));
                Assert.AreEqual("Implement interface", implementAction.Action.Title);
                ControlEditPlan implementationPlan = implementAction.EditPlan
                    ?? throw new InvalidDataException(
                        "MCP returned no interface implementation edit plan.");
                CallToolResult implementationApplyResult = await client.CallToolAsync(
                    "apply_edit_plan",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["planId"] = implementationPlan.PlanId.ToString("D")
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(implementationApplyResult.IsError);
                string implementedInterface = await File.ReadAllTextAsync(
                    implementInterfacePath,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.Contains(
                    "public string Run(int value)",
                    implementedInterface,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "throw new NotImplementedException();",
                    implementedInterface,
                    StringComparison.Ordinal);

                CallToolResult moveActionResult = await client.CallToolAsync(
                    "get_code_actions",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = moveTypePath,
                        ["startLine"] = 7,
                        ["startCharacter"] = 22,
                        ["endLine"] = 7,
                        ["endCharacter"] = 31,
                        ["kind"] = "refactor"
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                IReadOnlyList<ControlCodeActionPlan> moveActions =
                    GetStructuredCollection(moveActionResult, negotiatedProtocolVersion)
                        .Deserialize(
                            ControlJsonSerializerContext
                                .Default
                                .IReadOnlyListControlCodeActionPlan)
                    ?? throw new InvalidDataException(
                        "MCP returned no move-to-file refactoring preview.");
                ControlCodeActionPlan moveAction = Assert.ContainsSingle(
                    moveActions.Where(static candidate =>
                        candidate.Action.Title == "Move McpHelper to McpHelper.cs"));
                Assert.AreEqual("Move McpHelper to McpHelper.cs", moveAction.Action.Title);
                ControlEditPlan movePlan = moveAction.EditPlan
                    ?? throw new InvalidDataException(
                        "MCP returned no move-to-file refactoring edit plan.");
                Assert.HasCount(3, movePlan.Edit.DocumentChanges);
                Assert.HasCount(2, movePlan.Preconditions);
                ControlResourcePrecondition createPrecondition = Assert.ContainsSingle(
                    movePlan.Preconditions.Where(static precondition => !precondition.Exists));
                Assert.AreEqual(movedTypePath, createPrecondition.ResourcePath);
                CallToolResult moveApplyResult = await client.CallToolAsync(
                    "apply_edit_plan",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["planId"] = movePlan.PlanId.ToString("D")
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(moveApplyResult.IsError);
                Assert.IsTrue(File.Exists(movedTypePath));
                Assert.Contains(
                    "internal static class McpHelper",
                    await File.ReadAllTextAsync(
                        movedTypePath,
                        TestContext.CancellationToken).ConfigureAwait(false),
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "class McpHelper",
                    await File.ReadAllTextAsync(
                        moveTypePath,
                        TestContext.CancellationToken).ConfigureAwait(false),
                    StringComparison.Ordinal);

                await AssertResourcesPromptsAndMaintenanceAsync(
                    client, lsp.ProcessId, projectPath, documentPath).ConfigureAwait(false);
            }
            finally
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
