using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Csls.Control.Contracts;
using Csls.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

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
        string workerPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        string mcpPath = Environment.GetEnvironmentVariable("CSLS_TEST_MCP_PATH") ??
            Path.Combine(
                repositoryRoot,
                "artifacts",
                "bin",
                "Csls.Mcp",
                "debug",
                "csls-mcp.dll");
        string mcpWorkerPath =
            Environment.GetEnvironmentVariable("CSLS_TEST_MCP_WORKER_PATH") ??
            Path.Combine(
                repositoryRoot,
                "artifacts",
                "bin",
                "Csls.Mcp.Worker",
                "debug",
                "csls-mcp-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        Assert.IsTrue(File.Exists(mcpPath), $"MCP launcher not found at {mcpPath}.");
        Assert.IsTrue(
            File.Exists(mcpWorkerPath),
            $"MCP worker not found at {mcpWorkerPath}.");

        string fixturePath = Path.Combine(
            Path.GetTempPath(),
            $"csls-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string projectPath = Path.Combine(fixturePath, "Fixture.csproj");
            string documentPath = Path.Combine(fixturePath, "Program.cs");
            string importsPath = Path.Combine(fixturePath, "Imports.cs");
            string formattingPath = Path.Combine(fixturePath, "Formatting.cs");
            string stalePath = Path.Combine(fixturePath, "Stale.cs");
            string advancedPath = Path.Combine(fixturePath, "Advanced.cs");
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

            var lsp = LspProcessSession.Start(
                "csls-mcp-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "csls",
                initialization.GetProperty("serverInfo").GetProperty("name").GetString());
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);

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
                    WorkingDirectory = fixturePath,
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
                Assert.IsNotNull(client.NegotiatedProtocolVersion);

                IList<McpClientTool> tools = await client
                    .ListToolsAsync(cancellationToken: TestContext.CancellationToken)
                    .ConfigureAwait(false);
                McpClientTool sessionTool = tools.Single(static tool =>
                    tool.Name == "get_session");
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
                Assert.IsNull(definitionResult.IsError);
                Assert.IsTrue(definitionResult.StructuredContent.HasValue);
                IReadOnlyList<Location> definitions =
                    definitionResult.StructuredContent.Value.Deserialize(
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
                Assert.IsNull(declarationResult.IsError);
                Assert.IsTrue(declarationResult.StructuredContent.HasValue);
                Location declaration = Assert.ContainsSingle(
                    declarationResult.StructuredContent.Value.Deserialize(
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
                Assert.IsNull(typeDefinitionResult.IsError);
                Assert.IsTrue(typeDefinitionResult.StructuredContent.HasValue);
                Location typeDefinition = Assert.ContainsSingle(
                    typeDefinitionResult.StructuredContent.Value.Deserialize(
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
                Assert.IsNull(implementationResult.IsError);
                Assert.IsTrue(implementationResult.StructuredContent.HasValue);
                Location implementation = Assert.ContainsSingle(
                    implementationResult.StructuredContent.Value.Deserialize(
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
                Assert.IsNull(highlightsResult.IsError);
                Assert.IsTrue(highlightsResult.StructuredContent.HasValue);
                IReadOnlyList<DocumentHighlight> highlights = highlightsResult.StructuredContent
                    .Value.Deserialize(
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
                Assert.IsNull(referencesResult.IsError);
                Assert.IsTrue(referencesResult.StructuredContent.HasValue);
                IReadOnlyList<Location> references =
                    referencesResult.StructuredContent.Value.Deserialize(
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
                Assert.IsNull(documentSymbolsResult.IsError);
                Assert.IsTrue(documentSymbolsResult.StructuredContent.HasValue);
                IReadOnlyList<DocumentSymbol> documentSymbols =
                    documentSymbolsResult.StructuredContent.Value.Deserialize(
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
                Assert.IsNull(workspaceSymbolsResult.IsError);
                Assert.IsTrue(workspaceSymbolsResult.StructuredContent.HasValue);
                IReadOnlyList<WorkspaceSymbol> workspaceSymbols =
                    workspaceSymbolsResult.StructuredContent.Value.Deserialize(
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
                    rename.Edit.DocumentChanges);
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
                    formatting.Edit.DocumentChanges);
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
                Assert.IsNull(codeActionsResult.IsError);
                Assert.IsTrue(codeActionsResult.StructuredContent.HasValue);
                IReadOnlyList<ControlCodeActionPlan> codeActions = codeActionsResult
                    .StructuredContent.Value
                    .Deserialize(ControlJsonSerializerContext.Default.IReadOnlyListControlCodeActionPlan)
                    ?? throw new InvalidDataException("MCP returned no code action previews.");
                ControlCodeActionPlan organizeImports = Assert.ContainsSingle(codeActions);
                Assert.AreEqual("source.organizeImports", organizeImports.Action.Kind);
                Assert.IsNotNull(organizeImports.EditPlan);
                Assert.IsNotEmpty(organizeImports.EditPlan.Edit.DocumentChanges);

                IList<McpClientResource> resources = await client
                    .ListResourcesAsync(cancellationToken: TestContext.CancellationToken)
                    .ConfigureAwait(false);
                Assert.Contains(
                    "csls://session/current",
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
}
