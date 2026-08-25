using Csls.Control.Contracts;
using Csls.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies the official MCP C# SDK against a real csls worker and Unix-domain socket.
/// </summary>
[TestClass]
public sealed class McpLanguageServerTests
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

            var lsp = LspProcessSession.Start(
                "csls-mcp-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                repositoryRoot);
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

            string dotnetHost = EditorToolResolver.ResolveDotNetHost();
            Dictionary<string, string?> environment =
                StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnetHost);
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

            mcpArguments.Add("--session");
            mcpArguments.Add(lsp.ProcessId.ToString(CultureInfo.InvariantCulture));
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
                Assert.IsNotNull(annotations.ReadOnlyHint);
                Assert.IsTrue(annotations.ReadOnlyHint.Value);
                Assert.IsNotNull(annotations.DestructiveHint);
                Assert.IsFalse(annotations.DestructiveHint.Value);
                Assert.IsNotNull(annotations.OpenWorldHint);
                Assert.IsFalse(annotations.OpenWorldHint.Value);
                Assert.IsNotNull(annotations.IdempotentHint);
                Assert.IsTrue(annotations.IdempotentHint.Value);
                Assert.IsNotNull(sessionTool.ProtocolTool.OutputSchema);
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
                Assert.IsNotNull(applyAnnotations.ReadOnlyHint);
                Assert.IsFalse(applyAnnotations.ReadOnlyHint.Value);
                Assert.IsNotNull(applyAnnotations.DestructiveHint);
                Assert.IsTrue(applyAnnotations.DestructiveHint.Value);
                Assert.IsNotNull(applyAnnotations.IdempotentHint);
                Assert.IsFalse(applyAnnotations.IdempotentHint.Value);
                ToolAnnotations workspaceAnnotations = workspaceStateTool.ProtocolTool.Annotations
                    ?? throw new InvalidDataException(
                        "The workspace state tool has no MCP annotations.");
                Assert.IsNotNull(workspaceAnnotations.ReadOnlyHint);
                Assert.IsTrue(workspaceAnnotations.ReadOnlyHint.Value);
                Assert.IsNotNull(workspaceAnnotations.DestructiveHint);
                Assert.IsFalse(workspaceAnnotations.DestructiveHint.Value);
                ToolAnnotations restoreAnnotations = restoreWorkspaceTool.ProtocolTool.Annotations
                    ?? throw new InvalidDataException(
                        "The restore workspace tool has no MCP annotations.");
                Assert.IsNotNull(restoreAnnotations.ReadOnlyHint);
                Assert.IsFalse(restoreAnnotations.ReadOnlyHint.Value);
                Assert.IsNotNull(restoreAnnotations.DestructiveHint);
                Assert.IsFalse(restoreAnnotations.DestructiveHint.Value);
                ToolAnnotations clearAnnotations = clearCachesTool.ProtocolTool.Annotations
                    ?? throw new InvalidDataException(
                        "The clear caches tool has no MCP annotations.");
                Assert.IsNotNull(clearAnnotations.ReadOnlyHint);
                Assert.IsFalse(clearAnnotations.ReadOnlyHint.Value);
                Assert.IsNotNull(clearAnnotations.DestructiveHint);
                Assert.IsTrue(clearAnnotations.DestructiveHint.Value);

                CallToolResult sessionResult = await client.CallToolAsync(
                    "get_session",
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(sessionResult.IsError);
                Assert.IsTrue(sessionResult.StructuredContent.HasValue);
                ControlSessionInfo session = sessionResult.StructuredContent.Value.Deserialize(
                    ControlJsonSerializerContext.Default.ControlSessionInfo)
                    ?? throw new InvalidDataException("MCP returned no structured session value.");
                Assert.AreEqual(lsp.ProcessId, session.ProcessId);
                Assert.AreEqual("Running", session.LifecycleState);
                Assert.AreEqual(fixturePath, session.WorkspaceRoots.Single());

                CallToolResult workspaceStateResult = await client.CallToolAsync(
                    "get_workspace_state",
                    new Dictionary<string, object?>
                    {
                        ["includeDiagnostics"] = true
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(workspaceStateResult.IsError);
                Assert.IsTrue(workspaceStateResult.StructuredContent.HasValue);
                ControlDashboardSnapshot workspaceState = workspaceStateResult.StructuredContent
                    .Value.Deserialize(
                        ControlJsonSerializerContext.Default.ControlDashboardSnapshot)
                    ?? throw new InvalidDataException(
                        "MCP returned no structured workspace state.");
                Assert.IsTrue(workspaceState.DiagnosticsLoaded);
                Assert.Contains(
                    projectPath,
                    workspaceState.Projects.Select(static project => project.FilePath));
                Assert.Contains(
                    documentPath,
                    workspaceState.Documents.Select(static document => document.FilePath));
                Assert.Contains(
                    "CS0103",
                    workspaceState.Diagnostics.Select(static diagnostic => diagnostic.Id));

                CallToolResult hoverResult = await client.CallToolAsync(
                    "get_hover",
                    new Dictionary<string, object?>
                    {
                        ["documentPath"] = documentPath,
                        ["line"] = 6,
                        ["character"] = 10
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(hoverResult.IsError);
                Assert.IsTrue(hoverResult.StructuredContent.HasValue);
                ControlHoverResult hover = hoverResult.StructuredContent.Value.Deserialize(
                    ControlJsonSerializerContext.Default.ControlHoverResult)
                    ?? throw new InvalidDataException("MCP returned no structured hover value.");
                Assert.IsTrue(hover.Found);
                Assert.IsNotNull(hover.Hover);
                Assert.Contains("System.Console", hover.Hover.Contents.Value);

                CallToolResult diagnosticResult = await client.CallToolAsync(
                    "get_diagnostics",
                    new Dictionary<string, object?>
                    {
                        ["documentPath"] = documentPath
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(diagnosticResult.IsError);
                Assert.IsTrue(diagnosticResult.StructuredContent.HasValue);
                DocumentDiagnosticReport diagnosticReport =
                    diagnosticResult.StructuredContent.Value.Deserialize(
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
                        ["documentPath"] = documentPath,
                        ["line"] = 6,
                        ["character"] = 19
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(completionResult.IsError);
                Assert.IsTrue(completionResult.StructuredContent.HasValue);
                CompletionList completion = completionResult.StructuredContent.Value.Deserialize(
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
                        ["documentPath"] = advancedPath,
                        ["line"] = 19,
                        ["character"] = 17
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(selectionRangeResult.IsError);
                Assert.IsTrue(selectionRangeResult.StructuredContent.HasValue);
                SelectionRange selectionRange = selectionRangeResult.StructuredContent.Value
                    .Deserialize(ControlJsonSerializerContext.Default.SelectionRange)
                    ?? throw new InvalidDataException("MCP returned no selection range.");
                Assert.AreEqual(new Position(19, 15), selectionRange.Range.Start);
                Assert.IsNotNull(selectionRange.Parent);

                CallToolResult highlightsResult = await client.CallToolAsync(
                    "get_document_highlights",
                    new Dictionary<string, object?>
                    {
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
                Assert.IsNotNull(helper.Location.Range);
                Assert.AreEqual(new Position(10, 24), helper.Location.Range.Value.Start);

                CallToolResult signatureHelpResult = await client.CallToolAsync(
                    "get_signature_help",
                    new Dictionary<string, object?>
                    {
                        ["documentPath"] = documentPath,
                        ["line"] = 7,
                        ["character"] = 15
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(signatureHelpResult.IsError);
                Assert.IsTrue(signatureHelpResult.StructuredContent.HasValue);
                SignatureHelp signatureHelp = signatureHelpResult.StructuredContent.Value.Deserialize(
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
                        ["documentPath"] = documentPath,
                        ["line"] = 7,
                        ["character"] = 10,
                        ["newName"] = "RenamedHelper"
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(renameResult.IsError);
                Assert.IsTrue(renameResult.StructuredContent.HasValue);
                ControlEditPlan rename = renameResult.StructuredContent.Value.Deserialize(
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
                        ["documentPath"] = formattingPath,
                        ["tabSize"] = 4,
                        ["insertSpaces"] = true
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(formattingResult.IsError);
                Assert.IsTrue(formattingResult.StructuredContent.HasValue);
                ControlEditPlan formatting = formattingResult.StructuredContent.Value.Deserialize(
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
                        ["planId"] = formatting.PlanId.ToString("D")
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(applyResult.IsError);
                Assert.IsTrue(applyResult.StructuredContent.HasValue);
                ControlApplyEditPlanResult applied = applyResult.StructuredContent.Value.Deserialize(
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
                        ["planId"] = formatting.PlanId.ToString("D")
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsTrue(duplicateApplyResult.IsError);

                CallToolResult stalePreviewResult = await client.CallToolAsync(
                    "preview_formatting",
                    new Dictionary<string, object?>
                    {
                        ["documentPath"] = stalePath,
                        ["tabSize"] = 4,
                        ["insertSpaces"] = true
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(stalePreviewResult.IsError);
                Assert.IsTrue(stalePreviewResult.StructuredContent.HasValue);
                ControlEditPlan stalePlan = stalePreviewResult.StructuredContent.Value.Deserialize(
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
                ControlCodeActionPlan quickFix = Assert.ContainsSingle(quickFixes);
                Assert.AreEqual("Add using System.Text", quickFix.Action.Title);
                Assert.AreEqual("quickfix", quickFix.Action.Kind);
                ControlEditPlan quickFixPlan = quickFix.EditPlan
                    ?? throw new InvalidDataException("MCP returned no quick-fix edit plan.");
                CallToolResult quickFixApplyResult = await client.CallToolAsync(
                    "apply_edit_plan",
                    new Dictionary<string, object?>
                    {
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
                ControlCodeActionPlan implementAction = Assert.ContainsSingle(implementations);
                Assert.AreEqual("Implement interface", implementAction.Action.Title);
                ControlEditPlan implementationPlan = implementAction.EditPlan
                    ?? throw new InvalidDataException(
                        "MCP returned no interface implementation edit plan.");
                CallToolResult implementationApplyResult = await client.CallToolAsync(
                    "apply_edit_plan",
                    new Dictionary<string, object?>
                    {
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
                ControlCodeActionPlan moveAction = Assert.ContainsSingle(moveActions);
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

                IList<McpClientResource> resources = await client
                    .ListResourcesAsync(cancellationToken: TestContext.CancellationToken)
                    .ConfigureAwait(false);
                Assert.Contains(
                    "csls://session/current",
                    resources.Select(static resource => resource.Uri));
                Assert.Contains(
                    "csls://workspace/current",
                    resources.Select(static resource => resource.Uri));
                ReadResourceResult resourceResult = await client.ReadResourceAsync(
                    new Uri("csls://session/current", UriKind.Absolute),
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                TextResourceContents sessionResource = resourceResult.Contents
                    .OfType<TextResourceContents>()
                    .Single();
                ControlSessionInfo resourceSession = JsonSerializer.Deserialize(
                    sessionResource.Text,
                    ControlJsonSerializerContext.Default.ControlSessionInfo)
                    ?? throw new InvalidDataException("MCP returned no session resource value.");
                Assert.AreEqual(lsp.ProcessId, resourceSession.ProcessId);

                ReadResourceResult workspaceResourceResult = await client.ReadResourceAsync(
                    new Uri("csls://workspace/current", UriKind.Absolute),
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                TextResourceContents workspaceResource = workspaceResourceResult.Contents
                    .OfType<TextResourceContents>()
                    .Single();
                ControlDashboardSnapshot resourceWorkspace = JsonSerializer.Deserialize(
                    workspaceResource.Text,
                    ControlJsonSerializerContext.Default.ControlDashboardSnapshot)
                    ?? throw new InvalidDataException(
                        "MCP returned no workspace resource value.");
                Assert.AreEqual(lsp.ProcessId, resourceWorkspace.Session.ProcessId);

                IList<McpClientResourceTemplate> resourceTemplates = await client
                    .ListResourceTemplatesAsync(cancellationToken: TestContext.CancellationToken)
                    .ConfigureAwait(false);
                IEnumerable<string> resourceTemplateUris = resourceTemplates.Select(
                    static resource => resource.UriTemplate);
                Assert.Contains("csls://project{?path}", resourceTemplateUris);
                Assert.Contains("csls://document{?path}", resourceTemplateUris);
                Assert.Contains("csls://diagnostic{?path}", resourceTemplateUris);

                ReadResourceResult projectResourceResult = await client.ReadResourceAsync(
                    "csls://project{?path}",
                    new Dictionary<string, object?> { ["path"] = projectPath },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                ControlProjectInfo resourceProject = JsonSerializer.Deserialize(
                    projectResourceResult.Contents.OfType<TextResourceContents>().Single().Text,
                    ControlJsonSerializerContext.Default.ControlProjectInfo)
                    ?? throw new InvalidDataException("MCP returned no project resource value.");
                Assert.AreEqual(projectPath, resourceProject.FilePath);
                Assert.AreEqual("Fixture", resourceProject.Name);

                ReadResourceResult documentResourceResult = await client.ReadResourceAsync(
                    "csls://document{?path}",
                    new Dictionary<string, object?> { ["path"] = documentPath },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                ControlDocumentInfo resourceDocument = JsonSerializer.Deserialize(
                    documentResourceResult.Contents.OfType<TextResourceContents>().Single().Text,
                    ControlJsonSerializerContext.Default.ControlDocumentInfo)
                    ?? throw new InvalidDataException("MCP returned no document resource value.");
                Assert.AreEqual(documentPath, resourceDocument.FilePath);
                Assert.IsTrue(resourceDocument.IsOpen);

                ReadResourceResult diagnosticResourceResult = await client.ReadResourceAsync(
                    "csls://diagnostic{?path}",
                    new Dictionary<string, object?> { ["path"] = documentPath },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                DocumentDiagnosticReport resourceDiagnostics = JsonSerializer.Deserialize(
                    diagnosticResourceResult.Contents.OfType<TextResourceContents>().Single().Text,
                    ControlJsonSerializerContext.Default.DocumentDiagnosticReport)
                    ?? throw new InvalidDataException(
                        "MCP returned no diagnostic resource value.");
                Assert.Contains(
                    "CS0103",
                    resourceDiagnostics.Items?.Select(static diagnostic => diagnostic.Code) ?? []);

                IList<McpClientPrompt> prompts = await client
                    .ListPromptsAsync(cancellationToken: TestContext.CancellationToken)
                    .ConfigureAwait(false);
                IEnumerable<string> promptNames = prompts.Select(static prompt => prompt.Name);
                Assert.Contains("diagnose_csharp", promptNames);
                Assert.Contains("explain_symbol", promptNames);
                Assert.Contains("review_csharp", promptNames);
                Assert.Contains("refactor_csharp", promptNames);
                Assert.Contains("troubleshoot_csls", promptNames);
                GetPromptResult promptResult = await client.GetPromptAsync(
                    "diagnose_csharp",
                    new Dictionary<string, object?> { ["scope"] = documentPath },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNotEmpty(promptResult.Messages);

                ControlWorkspaceOperationResult clearResult =
                    await CallWorkspaceOperationAsync(
                        client,
                        "clear_caches",
                        TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("clear-cache", clearResult.Operation);
                Assert.AreEqual(
                    clearResult.PreviousGeneration,
                    clearResult.CurrentGeneration);
                Assert.IsGreaterThan(0, clearResult.ClearedCacheEntryCount);

                ControlWorkspaceOperationResult reloadResult =
                    await CallWorkspaceOperationAsync(
                        client,
                        "reload_workspace",
                        TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("reload", reloadResult.Operation);
                Assert.AreEqual(
                    reloadResult.PreviousGeneration + 1,
                    reloadResult.CurrentGeneration);

                ControlWorkspaceOperationResult restartResult =
                    await CallWorkspaceOperationAsync(
                        client,
                        "restart_build_hosts",
                        TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("restart-build-host", restartResult.Operation);
                Assert.AreEqual(
                    restartResult.PreviousGeneration + 1,
                    restartResult.CurrentGeneration);
                Assert.IsGreaterThan(0, restartResult.RestartedBuildHostCount);

                ControlWorkspaceOperationResult restoreResult =
                    await CallWorkspaceOperationAsync(
                        client,
                        "restore_workspace",
                        TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("restore", restoreResult.Operation);
                Assert.AreEqual(
                    restoreResult.PreviousGeneration + 1,
                    restoreResult.CurrentGeneration);
                Assert.AreEqual(1, restoreResult.RestoredEntryPointCount);
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
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    /// <summary>
    /// Cancels and traces a live Roslyn analyzer request through official MCP client calls.
    /// </summary>
    [TestMethod]
    public async Task McpCancelsLiveAnalyzerRequestAndReturnsTrace()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string workerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        string mcpPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Mcp",
            "debug",
            "csls-mcp.dll");
        string mcpWorkerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Mcp.Worker",
            "debug",
            "csls-mcp-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        Assert.IsTrue(File.Exists(mcpPath), $"MCP launcher not found at {mcpPath}.");
        Assert.IsTrue(File.Exists(mcpWorkerPath), $"MCP worker not found at {mcpWorkerPath}.");
        CancellationProbeFixture fixture = await CancellationProbeFixture.CreateAsync(
            repositoryRoot,
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var lsp = LspProcessSession.Start(
            "csls-mcp-cancellation-worker",
            EditorToolResolver.ResolveDotNetHost(),
            [workerPath],
            repositoryRoot);
        await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
        await lsp.InitializeAsync(
            fixture.RootPath,
            TestContext.CancellationToken).ConfigureAwait(false);
        await lsp.OpenDocumentAsync(
            fixture.DocumentPath,
            CancellationProbeFixture.DocumentText).ConfigureAwait(false);

        string dotnetHost = EditorToolResolver.ResolveDotNetHost();
        Dictionary<string, string?> environment =
            StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnetHost);
        environment["CSLS_MCP_WORKER_PATH"] = mcpWorkerPath;
        bool isManagedLauncher = string.Equals(
            Path.GetExtension(mcpPath),
            ".dll",
            StringComparison.OrdinalIgnoreCase);
        List<string> arguments = [];
        if (isManagedLauncher)
        {
            arguments.Add(mcpPath);
        }

        arguments.Add("--session");
        arguments.Add(lsp.ProcessId.ToString(CultureInfo.InvariantCulture));
        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Command = isManagedLauncher ? dotnetHost : mcpPath,
                Arguments = arguments,
                Name = "csls-mcp-request-control",
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
            IList<McpClientTool> tools = await client.ListToolsAsync(
                cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
            McpClientTool listTool = tools.Single(static tool => tool.Name == "list_requests");
            McpClientTool cancelTool = tools.Single(static tool => tool.Name == "cancel_request");
            McpClientTool startTool = tools.Single(static tool => tool.Name == "start_trace");
            McpClientTool stopTool = tools.Single(static tool => tool.Name == "stop_trace");
            Assert.IsNotNull(listTool.ProtocolTool.OutputSchema);
            Assert.IsNotNull(cancelTool.ProtocolTool.OutputSchema);
            Assert.IsNotNull(startTool.ProtocolTool.OutputSchema);
            Assert.IsNotNull(stopTool.ProtocolTool.OutputSchema);
            ToolAnnotations listAnnotations = listTool.ProtocolTool.Annotations
                ?? throw new InvalidDataException("The request list tool has no annotations.");
            Assert.IsNotNull(listAnnotations.ReadOnlyHint);
            Assert.IsTrue(listAnnotations.ReadOnlyHint.Value);
            Assert.IsNotNull(listAnnotations.DestructiveHint);
            Assert.IsFalse(listAnnotations.DestructiveHint.Value);
            Assert.IsNotNull(listAnnotations.IdempotentHint);
            Assert.IsTrue(listAnnotations.IdempotentHint.Value);
            Assert.IsNotNull(listAnnotations.OpenWorldHint);
            Assert.IsFalse(listAnnotations.OpenWorldHint.Value);
            ToolAnnotations cancelAnnotations = cancelTool.ProtocolTool.Annotations
                ?? throw new InvalidDataException("The request cancellation tool has no annotations.");
            Assert.IsNotNull(cancelAnnotations.ReadOnlyHint);
            Assert.IsFalse(cancelAnnotations.ReadOnlyHint.Value);
            Assert.IsNotNull(cancelAnnotations.DestructiveHint);
            Assert.IsTrue(cancelAnnotations.DestructiveHint.Value);
            Assert.IsNotNull(cancelAnnotations.IdempotentHint);
            Assert.IsTrue(cancelAnnotations.IdempotentHint.Value);
            Assert.IsNotNull(cancelAnnotations.OpenWorldHint);
            Assert.IsFalse(cancelAnnotations.OpenWorldHint.Value);
            ToolAnnotations startAnnotations = startTool.ProtocolTool.Annotations
                ?? throw new InvalidDataException("The trace start tool has no annotations.");
            Assert.IsNotNull(startAnnotations.ReadOnlyHint);
            Assert.IsFalse(startAnnotations.ReadOnlyHint.Value);
            Assert.IsNotNull(startAnnotations.DestructiveHint);
            Assert.IsFalse(startAnnotations.DestructiveHint.Value);
            Assert.IsNotNull(startAnnotations.IdempotentHint);
            Assert.IsFalse(startAnnotations.IdempotentHint.Value);
            Assert.IsNotNull(startAnnotations.OpenWorldHint);
            Assert.IsFalse(startAnnotations.OpenWorldHint.Value);
            ToolAnnotations stopAnnotations = stopTool.ProtocolTool.Annotations
                ?? throw new InvalidDataException("The trace stop tool has no annotations.");
            Assert.IsNotNull(stopAnnotations.ReadOnlyHint);
            Assert.IsFalse(stopAnnotations.ReadOnlyHint.Value);
            Assert.IsNotNull(stopAnnotations.DestructiveHint);
            Assert.IsFalse(stopAnnotations.DestructiveHint.Value);
            Assert.IsNotNull(stopAnnotations.IdempotentHint);
            Assert.IsFalse(stopAnnotations.IdempotentHint.Value);
            Assert.IsNotNull(stopAnnotations.OpenWorldHint);
            Assert.IsFalse(stopAnnotations.OpenWorldHint.Value);

            CallToolResult invalidCancellation = await client.CallToolAsync(
                "cancel_request",
                new Dictionary<string, object?>
                {
                    ["correlationId"] = "not-a-correlation-id"
                },
                cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(invalidCancellation.IsError);
            Assert.IsNull(invalidCancellation.StructuredContent);

            CallToolResult startResult = await client.CallToolAsync(
                "start_trace",
                cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNull(startResult.IsError);
            Assert.IsTrue(startResult.StructuredContent.HasValue);
            ControlTraceInfo startedTrace = startResult.StructuredContent.Value.Deserialize(
                ControlJsonSerializerContext.Default.ControlTraceInfo)
                ?? throw new InvalidDataException("MCP returned no started trace value.");
            Assert.IsTrue(startedTrace.IsActive);
            Assert.IsNotNull(startedTrace.TraceId);

            Task<CallToolResult> diagnosticRequest = client.CallToolAsync(
                "get_diagnostics",
                new Dictionary<string, object?>
                {
                    ["documentPath"] = fixture.DocumentPath
                },
                cancellationToken: TestContext.CancellationToken).AsTask();
            await FileTextWaiter.WaitAsync(
                fixture.MarkerPath,
                "started",
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);
            CallToolResult listResult = await client.CallToolAsync(
                "list_requests",
                cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNull(listResult.IsError);
            Assert.IsTrue(listResult.StructuredContent.HasValue);
            ControlRequestSchedulerInfo requests = listResult.StructuredContent.Value.Deserialize(
                ControlJsonSerializerContext.Default.ControlRequestSchedulerInfo)
                ?? throw new InvalidDataException("MCP returned no request scheduler value.");
            ControlRequestInfo request = requests.ActiveRequests.Single(static item =>
                item.Name == "textDocument/diagnostic");
            Assert.AreEqual("Running", request.Status);
            Assert.IsTrue(requests.Trace.IsActive);

            CallToolResult cancelResult = await client.CallToolAsync(
                "cancel_request",
                new Dictionary<string, object?>
                {
                    ["correlationId"] = request.CorrelationId.ToString("D")
                },
                cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNull(cancelResult.IsError);
            Assert.IsTrue(cancelResult.StructuredContent.HasValue);
            ControlCancelRequestResult cancellation = cancelResult.StructuredContent.Value.Deserialize(
                ControlJsonSerializerContext.Default.ControlCancelRequestResult)
                ?? throw new InvalidDataException("MCP returned no request cancellation value.");
            Assert.AreEqual(request.CorrelationId, cancellation.CorrelationId);
            Assert.IsTrue(cancellation.CancellationRequested);
            await FileTextWaiter.WaitAsync(
                fixture.MarkerPath,
                "canceled",
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);
            CallToolResult diagnosticResult = await diagnosticRequest.ConfigureAwait(false);
            Assert.IsTrue(diagnosticResult.IsError);

            CallToolResult stopResult = await client.CallToolAsync(
                "stop_trace",
                cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNull(stopResult.IsError);
            Assert.IsTrue(stopResult.StructuredContent.HasValue);
            ControlTraceInfo stoppedTrace = stopResult.StructuredContent.Value.Deserialize(
                ControlJsonSerializerContext.Default.ControlTraceInfo)
                ?? throw new InvalidDataException("MCP returned no stopped trace value.");
            Assert.IsFalse(stoppedTrace.IsActive);
            Assert.AreEqual(startedTrace.TraceId, stoppedTrace.TraceId);
            ControlTraceEntry entry = stoppedTrace.Entries.Single(item =>
                item.CorrelationId == request.CorrelationId);
            Assert.AreEqual("Canceled", entry.Status);
            Assert.AreEqual(request.WorkspaceGeneration, entry.WorkspaceGeneration);
            Assert.IsTrue(entry.IsCancellationRequested);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        string diagnostics = await lsp.ShutdownAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
    }

    private static async Task<ControlWorkspaceOperationResult> CallWorkspaceOperationAsync(
        McpClient client,
        string toolName,
        CancellationToken cancellationToken)
    {
        CallToolResult result = await client.CallToolAsync(
            toolName,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Assert.IsNull(result.IsError);
        Assert.IsTrue(result.StructuredContent.HasValue);
        return result.StructuredContent.Value.Deserialize(
            ControlJsonSerializerContext.Default.ControlWorkspaceOperationResult)
            ?? throw new InvalidDataException(
                $"MCP returned no workspace operation result for {toolName}.");
    }

    private static JsonElement GetStructuredCollection(
        CallToolResult result,
        string negotiatedProtocolVersion)
    {
        Assert.IsNull(result.IsError);
        Assert.IsTrue(result.StructuredContent.HasValue);
        JsonElement structuredContent = result.StructuredContent.Value;
        if (string.CompareOrdinal(
                negotiatedProtocolVersion,
                NaturalStructuredOutputProtocolVersion) >= 0)
        {
            Assert.AreEqual(JsonValueKind.Array, structuredContent.ValueKind);
            return structuredContent;
        }

        Assert.AreEqual(JsonValueKind.Object, structuredContent.ValueKind);
        Assert.IsTrue(structuredContent.TryGetProperty("result", out JsonElement collection));
        Assert.AreEqual(JsonValueKind.Array, collection.ValueKind);
        return collection;
    }

    private const string NaturalStructuredOutputProtocolVersion = "2026-07-28";

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private const string DocumentText = """
        namespace Fixture;

        public static class Program
        {
            public static void Main()
            {
                Console.WriteLine(Missing);
                Helper(1);
            }

            private static void Helper(int value)
            {
                Console.WriteLine( value );
            }
        }
        """;

    private const string ImportsText = """
        using System.Text;
        using System;

        namespace Fixture;

        public static class Imports;
        """;

    private const string FormattingText = """
        namespace Fixture;

        public static class Formatting{public static int Add(int left,int right)=>left+right;}
        """;

    private const string MissingUsingText = """
        namespace Fixture;

        public static class MissingUsing
        {
            public static string Build()
            {
                var builder = new StringBuilder();
                return builder.ToString();
            }
        }
        """;

    private const string ImplementInterfaceText = """
        namespace InterfaceActions;

        public interface IRunner
        {
            string Run(int value);
        }

        public sealed class Runner : IRunner
        {
        }
        """;

    private const string AdvancedDocumentText = """
        namespace Fixture;

        public interface IRunner
        {
            void Execute();
        }

        public sealed class Runner : IRunner
        {
            public void Execute()
            {
            }
        }

        public static class AdvancedProgram
        {
            public static void Run()
            {
                IRunner runner = new Runner();
                runner.Execute();
                runner = new Runner();
                _ = runner;
            }
        }
        """;

    private const string MoveTypeDocumentText = """
        namespace Fixture;

        public static class MoveTypes
        {
            public static int Read() => McpHelper.Value;
        }

        internal static class McpHelper
        {
            public static int Value => 42;
        }
        """;
}
