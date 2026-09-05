---
title: Contract reference
description: Generated public LSP, control, and debugger RPC contract type index.
---

These public wire-contract types are generated from the compiled assemblies and their XML documentation.

## Csls.Protocol

| Type | Description |
| --- | --- |
| `Csls.Protocol.CSharpDebugInfo` | Describes live language-server state for diagnostics and test synchronization. |
| `Csls.Protocol.CSharpDebugRequestInfo` | Describes one queued or running language-server request. |
| `Csls.Protocol.CSharpDebugRequestQueueInfo` | Describes one bounded observation of request scheduling and execution timings. |
| `Csls.Protocol.CSharpDebugRequestStatisticsInfo` | Describes cumulative execution timings for one request name. |
| `Csls.Protocol.CSharpDebugWorkspaceFolderInfo` | Describes one loaded workspace folder in the current Roslyn generation. |
| `Csls.Protocol.CSharpDebugWorkspaceInfo` | Describes the current workspace lifecycle and immutable Roslyn generation. |
| `Csls.Protocol.CSharpExperimentalServerCapabilities` | Advertises csls extensions that expose virtual C# source documents. |
| `Csls.Protocol.CSharpMetadataParams` | Identifies one virtual C# document whose source text is requested. |
| `Csls.Protocol.CSharpMetadataResponse` | Describes source text for one generated or metadata-backed C# document. |
| `Csls.Protocol.CSharpSemanticTokensLegend` | Defines the portable LSP semantic-token vocabulary emitted for C# documents. |
| `Csls.Protocol.CSharpWorkspaceDocumentInfo` | Describes one loaded source document for an editor client. |
| `Csls.Protocol.CSharpWorkspaceFolderInfo` | Describes one loaded workspace folder for an editor client. |
| `Csls.Protocol.CSharpWorkspaceInfo` | Describes the current Roslyn workspace generation for an editor client. |
| `Csls.Protocol.CSharpWorkspaceOperationInfo` | Describes one completed workspace maintenance operation requested by an editor. |
| `Csls.Protocol.CSharpWorkspaceProjectInfo` | Describes one loaded Roslyn project for an editor client. |
| `Csls.Protocol.CallHierarchyIncomingCall` | Describes one caller and its call sites within that caller. |
| `Csls.Protocol.CallHierarchyIncomingCallsParams` | Identifies the call-hierarchy item whose callers should be returned. |
| `Csls.Protocol.CallHierarchyItem` | Describes one callable source declaration in a call hierarchy. |
| `Csls.Protocol.CallHierarchyOutgoingCall` | Describes one callee and its call sites within the requesting caller. |
| `Csls.Protocol.CallHierarchyOutgoingCallsParams` | Identifies the call-hierarchy item whose callees should be returned. |
| `Csls.Protocol.CallHierarchyPrepareParams` | Identifies the document position for a call-hierarchy prepare request. |
| `Csls.Protocol.ClientInfo` | Describes the editor or tool connected to the language server. |
| `Csls.Protocol.CodeAction` | Describes one editor-visible source transformation and its concrete edit. |
| `Csls.Protocol.CodeActionContext` | Describes diagnostics and action categories requested by an editor. |
| `Csls.Protocol.CodeActionOptions` | Advertises the concrete code-action categories implemented by the server. |
| `Csls.Protocol.CodeActionParams` | Identifies a document range and the editor's requested code-action context. |
| `Csls.Protocol.CodeLens` | Describes one source declaration annotation resolved on demand by the client. |
| `Csls.Protocol.CodeLensData` | Preserves declaration identity and coordinates required to resolve a code lens. |
| `Csls.Protocol.CodeLensOptions` | Describes the server's code-lens resolution behavior. |
| `Csls.Protocol.CodeLensParams` | Identifies the source document requested for code-lens discovery. |
| `Csls.Protocol.CompletionContext` | Describes how the client triggered one completion request. |
| `Csls.Protocol.CompletionItem` | Describes one bounded Roslyn completion candidate and its exact document edits. |
| `Csls.Protocol.CompletionItemData` | Identifies one completion candidate for deterministic lazy resolution. |
| `Csls.Protocol.CompletionItemKind` | Identifies the semantic editor icon category for one completion item. |
| `Csls.Protocol.CompletionList` | Returns a bounded ordered set of completion candidates. |
| `Csls.Protocol.CompletionOptions` | Advertises completion triggers and optional item-resolution support. |
| `Csls.Protocol.CompletionParams` | Requests completion candidates at one UTF-16 document position. |
| `Csls.Protocol.CompletionTriggerKind` | Identifies how an LSP client triggered a completion request. |
| `Csls.Protocol.ConfigurationItem` | Identifies one client configuration section and its optional resource scope. |
| `Csls.Protocol.ConfigurationParams` | Contains the ordered configuration sections requested from an LSP client. |
| `Csls.Protocol.CreateFile` | Creates one file or directory before later workspace-edit operations are applied. |
| `Csls.Protocol.CreateFileOptions` | Controls how a workspace create operation handles an existing target resource. |
| `Csls.Protocol.CreateFilesParams` | Carries files and folders created through the client. |
| `Csls.Protocol.DeleteFile` | Deletes one file or directory before later workspace-edit operations are applied. |
| `Csls.Protocol.DeleteFileOptions` | Controls how a workspace delete operation handles directories and missing resources. |
| `Csls.Protocol.DeleteFilesParams` | Carries files and folders deleted through the client. |
| `Csls.Protocol.Diagnostic` | Describes one compiler or analyzer finding associated with a source range. |
| `Csls.Protocol.DiagnosticOptions` | Advertises the server's pull-diagnostic behavior to an LSP client. |
| `Csls.Protocol.DiagnosticServerCancellationData` | Tells a pull-diagnostic client whether it should repeat a server-cancelled request. |
| `Csls.Protocol.DiagnosticSeverity` | Identifies the editor-facing severity assigned to one language diagnostic. |
| `Csls.Protocol.DiagnosticTag` | Identifies additional editor behavior associated with one diagnostic. |
| `Csls.Protocol.DidChangeConfigurationParams` | Contains configuration settings pushed by an LSP client. |
| `Csls.Protocol.DidChangeTextDocumentParams` | Contains ordered content changes for one opened versioned text document. |
| `Csls.Protocol.DidChangeWatchedFilesParams` | Describes file-system changes observed by the LSP client. |
| `Csls.Protocol.DidChangeWatchedFilesRegistrationOptions` | Configures dynamically registered workspace file-system watchers. |
| `Csls.Protocol.DidChangeWorkspaceFoldersParams` | Contains one ordered workspace-folder change notification. |
| `Csls.Protocol.DidCloseTextDocumentParams` | Reports that a client no longer owns an open text document overlay. |
| `Csls.Protocol.DidOpenTextDocumentParams` | Contains the document opened by the client. |
| `Csls.Protocol.DidSaveTextDocumentParams` | Reports that an opened text document was persisted by the client. |
| `Csls.Protocol.DocumentDiagnosticParams` | Requests current diagnostics for one versioned workspace document. |
| `Csls.Protocol.DocumentDiagnosticReport` | Returns a complete or unchanged pull-diagnostic result for one document. |
| `Csls.Protocol.DocumentFormattingParams` | Identifies a document and the editor preferences used to format it. |
| `Csls.Protocol.DocumentHighlight` | Describes one semantic occurrence of a symbol in a source document. |
| `Csls.Protocol.DocumentHighlightKind` | Identifies the semantic access performed by one document highlight. |
| `Csls.Protocol.DocumentLink` | Identifies one source range that opens an internal or external resource. |
| `Csls.Protocol.DocumentLinkOptions` | Advertises whether document-link targets require a later resolve request. |
| `Csls.Protocol.DocumentLinkParams` | Identifies the document whose navigable resource links are requested. |
| `Csls.Protocol.DocumentOnTypeFormattingOptions` | Advertises the characters that trigger formatting while a document is edited. |
| `Csls.Protocol.DocumentOnTypeFormattingParams` | Identifies a document position and the character that triggered formatting. |
| `Csls.Protocol.DocumentRangeFormattingParams` | Identifies a document range and the editor preferences used to format it. |
| `Csls.Protocol.DocumentSymbol` | Describes one hierarchical declaration within a source document. |
| `Csls.Protocol.DocumentSymbolParams` | Identifies the document whose declaration hierarchy is requested. |
| `Csls.Protocol.DocumentUri` | Represents an absolute URI used by the Language Server Protocol. |
| `Csls.Protocol.DocumentUriJsonConverter` | Converts document URI values to and from their LSP string representation. |
| `Csls.Protocol.ExperimentalServerCapabilities` | Groups negotiated server extensions outside the stable LSP capability set. |
| `Csls.Protocol.FileChangeType` | Identifies how one watched workspace file changed. |
| `Csls.Protocol.FileCreate` | Identifies one file or folder created by the client. |
| `Csls.Protocol.FileDelete` | Identifies one file or folder deleted by the client. |
| `Csls.Protocol.FileEvent` | Describes one file-system change reported by an LSP client. |
| `Csls.Protocol.FileOperationFilter` | Selects workspace file operations by URI scheme and path pattern. |
| `Csls.Protocol.FileOperationOptions` | Advertises the workspace file-operation notifications consumed by the server. |
| `Csls.Protocol.FileOperationPattern` | Describes paths that trigger one workspace file operation. |
| `Csls.Protocol.FileOperationPatternKind` | Defines whether a file-operation glob matches files or folders. |
| `Csls.Protocol.FileOperationPatternOptions` | Configures matching for one file-operation glob. |
| `Csls.Protocol.FileOperationRegistrationOptions` | Registers the workspace paths relevant to one file-operation method. |
| `Csls.Protocol.FileRename` | Identifies one file or folder renamed by the client. |
| `Csls.Protocol.FileSystemWatcher` | Describes one workspace file-system glob requested from an LSP client. |
| `Csls.Protocol.FoldingRange` | Describes one foldable source range using zero-based UTF-16 positions. |
| `Csls.Protocol.FoldingRangeKind` | Defines the standard LSP semantic categories for folding ranges. |
| `Csls.Protocol.FoldingRangeParams` | Identifies one document whose foldable source ranges are requested. |
| `Csls.Protocol.FormattingOptions` | Carries editor indentation and final-line preferences for document formatting. |
| `Csls.Protocol.HierarchyItemData` | Preserves immutable source coordinates required to expand a hierarchy item. |
| `Csls.Protocol.Hover` | Contains hover information and the optional source range it describes. |
| `Csls.Protocol.ILspClientConnection` | Sends the supported server-to-client LSP requests and notifications. |
| `Csls.Protocol.ILspRpcTarget` | Defines every explicitly registered LSP entry point implemented by the server engine. |
| `Csls.Protocol.InitializeParams` | Contains the client's initialization request and negotiated capabilities. |
| `Csls.Protocol.InitializeResult` | Returns negotiated server capabilities and implementation information. |
| `Csls.Protocol.InitializedParams` | Represents the parameter object for the initialized notification. |
| `Csls.Protocol.InlayHint` | Describes one type or parameter annotation rendered inside source text. |
| `Csls.Protocol.InlayHintData` | Preserves immutable semantic coordinates required to resolve an inlay hint. |
| `Csls.Protocol.InlayHintDataKind` | Identifies the server-owned data required to resolve an inlay hint. |
| `Csls.Protocol.InlayHintKind` | Identifies the semantic category of an inlay hint. |
| `Csls.Protocol.InlayHintOptions` | Describes the server's inlay-hint resolve behavior. |
| `Csls.Protocol.InlayHintParams` | Identifies the visible document range for an inlay-hint request. |
| `Csls.Protocol.InsertTextFormat` | Identifies whether completion insertion text is plain text or an LSP snippet. |
| `Csls.Protocol.LinkedEditingRangeParams` | Selects one document position for a linked editing range request. |
| `Csls.Protocol.LinkedEditingRanges` | Describes equal, non-overlapping source ranges that clients edit together. |
| `Csls.Protocol.Location` | Identifies one source URI and exact UTF-16 range. |
| `Csls.Protocol.LspCommand` | Describes one editor command and its JSON-compatible arguments. |
| `Csls.Protocol.LspJson` | Creates the shared source-generated JSON configuration used by LSP transports. |
| `Csls.Protocol.LspJsonSerializerContext` | Provides generated System.Text.Json metadata for every registered LSP contract. |
| `Csls.Protocol.LspServerCancelledException` | Represents an LSP request that the server cancels because its input snapshot became stale. |
| `Csls.Protocol.MarkupContent` | Contains markdown or plain-text content returned to an LSP client. |
| `Csls.Protocol.Moniker` | Identifies one C# symbol across documents, projects, and indexed repositories. |
| `Csls.Protocol.MonikerKind` | Identifies how a moniker-bearing symbol participates in the current project. |
| `Csls.Protocol.MonikerParams` | Selects the symbol for a text document moniker request. |
| `Csls.Protocol.NegotiatedClientCapabilities` | Contains the client capability values consumed by the language server. |
| `Csls.Protocol.OptionalVersionedTextDocumentIdentifier` | Identifies a text document with an optional open-document version precondition. |
| `Csls.Protocol.ParameterInformation` | Describes one parameter within a callable signature label. |
| `Csls.Protocol.Position` | Identifies a zero-based UTF-16 line and character position in a text document. |
| `Csls.Protocol.PrepareRenameResult` | Describes the editable source range and current symbol name for rename. |
| `Csls.Protocol.PreviousResultId` | Identifies one workspace document result already retained by an LSP client. |
| `Csls.Protocol.PublishDiagnosticsParams` | Publishes the complete diagnostic state for one client document version. |
| `Csls.Protocol.Range` | Represents a half-open range between two UTF-16 document positions. |
| `Csls.Protocol.ReferenceContext` | Configures declaration inclusion for one reference search. |
| `Csls.Protocol.ReferenceParams` | Requests symbol references at one UTF-16 document position. |
| `Csls.Protocol.Registration` | Describes one dynamically registered LSP capability. |
| `Csls.Protocol.RegistrationParams` | Describes capabilities dynamically registered with an LSP client. |
| `Csls.Protocol.RenameFile` | Renames one file or directory before later workspace-edit operations are applied. |
| `Csls.Protocol.RenameFileOptions` | Controls how a workspace rename operation handles an existing destination resource. |
| `Csls.Protocol.RenameFilesParams` | Carries files and folders renamed through the client. |
| `Csls.Protocol.RenameOptions` | Advertises server-side rename validation before an edit is requested. |
| `Csls.Protocol.RenameParams` | Identifies a symbol and the new identifier requested by the client. |
| `Csls.Protocol.SelectionRange` | Describes one source selection and its next enclosing syntax selection. |
| `Csls.Protocol.SelectionRangeParams` | Identifies one document and the positions requiring syntax selection hierarchies. |
| `Csls.Protocol.SemanticTokens` | Contains a complete relative-encoded semantic-token sequence. |
| `Csls.Protocol.SemanticTokensDeltaParams` | Identifies a document and prior semantic-token result to update. |
| `Csls.Protocol.SemanticTokensDeltaResult` | Contains either delta edits or a complete fallback semantic-token sequence. |
| `Csls.Protocol.SemanticTokensEdit` | Replaces one contiguous region of a prior semantic-token integer sequence. |
| `Csls.Protocol.SemanticTokensFullOptions` | Advertises complete-document semantic-token behavior. |
| `Csls.Protocol.SemanticTokensLegend` | Describes the token types and modifier bit positions used by semantic-token results. |
| `Csls.Protocol.SemanticTokensOptions` | Advertises the server semantic-token legend and supported request forms. |
| `Csls.Protocol.SemanticTokensParams` | Identifies a document whose complete semantic tokens are requested. |
| `Csls.Protocol.ServerCapabilities` | Advertises only the LSP capabilities implemented by the running server. |
| `Csls.Protocol.ServerInfo` | Describes the language server implementation and version. |
| `Csls.Protocol.SignatureHelp` | Carries overload labels and the client's current callable argument state. |
| `Csls.Protocol.SignatureHelpContext` | Describes the client state that triggered a signature help request. |
| `Csls.Protocol.SignatureHelpOptions` | Advertises the characters that trigger or retrigger signature help. |
| `Csls.Protocol.SignatureHelpParams` | Identifies a document position and client context for signature help. |
| `Csls.Protocol.SignatureHelpTriggerKind` | Identifies why a client requested signature help. |
| `Csls.Protocol.SignatureInformation` | Describes one callable overload and its ordered parameters. |
| `Csls.Protocol.SymbolKind` | Identifies the editor-visible category of a program symbol. |
| `Csls.Protocol.TextDocumentContentChangeEvent` | Applies one ordered full-text or incremental mutation to an opened document. |
| `Csls.Protocol.TextDocumentEdit` | Groups ordered text edits under one document version precondition. |
| `Csls.Protocol.TextDocumentIdentifier` | Identifies a text document by its URI. |
| `Csls.Protocol.TextDocumentItem` | Contains the complete contents and identity of an opened text document. |
| `Csls.Protocol.TextDocumentPositionParams` | Identifies a position within a text document. |
| `Csls.Protocol.TextDocumentSaveReason` | Identifies why an editor is saving a text document. |
| `Csls.Protocol.TextDocumentSyncKind` | Specifies how text document content changes are synchronized. |
| `Csls.Protocol.TextDocumentSyncOptions` | Describes the server's text document synchronization behavior. |
| `Csls.Protocol.TextEdit` | Replaces one half-open UTF-16 document range with new text. |
| `Csls.Protocol.TypeHierarchyItem` | Describes one source type declaration in a type hierarchy. |
| `Csls.Protocol.TypeHierarchyPrepareParams` | Identifies the document position for a type-hierarchy prepare request. |
| `Csls.Protocol.TypeHierarchySubtypesParams` | Identifies the type-hierarchy item whose direct subtypes should be returned. |
| `Csls.Protocol.TypeHierarchySupertypesParams` | Identifies the type-hierarchy item whose direct supertypes should be returned. |
| `Csls.Protocol.UniquenessLevel` | Identifies the scope in which a moniker identifier is unique. |
| `Csls.Protocol.VersionedTextDocumentIdentifier` | Identifies a text document and the client version after a content mutation. |
| `Csls.Protocol.WatchKind` | Selects the file-system changes requested from an LSP client. |
| `Csls.Protocol.WillSaveTextDocumentParams` | Identifies a document that the editor is about to save. |
| `Csls.Protocol.WorkDoneProgressBegin` | Starts one client-visible work-done progress sequence. |
| `Csls.Protocol.WorkDoneProgressCreateParams` | Requests client registration of one server-generated work-done progress token. |
| `Csls.Protocol.WorkDoneProgressEnd` | Completes one client-visible work-done progress sequence. |
| `Csls.Protocol.WorkDoneProgressParams` | Carries one typed work-done value through an LSP progress notification. |
| `Csls.Protocol.WorkDoneProgressReport` | Updates one active client-visible work-done progress sequence. |
| `Csls.Protocol.WorkDoneProgressValue` | Represents one typed value carried by an LSP work-done progress notification. |
| `Csls.Protocol.WorkspaceDiagnosticParams` | Requests current diagnostics for every document in the loaded workspace. |
| `Csls.Protocol.WorkspaceDiagnosticProgressParams` | Carries one workspace diagnostic partial result through an LSP progress notification. |
| `Csls.Protocol.WorkspaceDiagnosticReport` | Returns bounded document diagnostic results for one loaded workspace snapshot. |
| `Csls.Protocol.WorkspaceDocumentChange` | Represents one ordered text edit or filesystem resource operation in a workspace edit. |
| `Csls.Protocol.WorkspaceDocumentChangeJsonConverter` | Converts the mixed LSP workspace document-change array without reflection metadata. |
| `Csls.Protocol.WorkspaceDocumentDiagnosticReport` | Returns one complete or unchanged document result from a workspace diagnostic pull. |
| `Csls.Protocol.WorkspaceEdit` | Carries version-aware text edits spanning one or more workspace documents. |
| `Csls.Protocol.WorkspaceFolder` | Identifies a client workspace folder and its display name. |
| `Csls.Protocol.WorkspaceFoldersChangeEvent` | Describes workspace folders added to and removed from an LSP session. |
| `Csls.Protocol.WorkspaceFoldersServerCapabilities` | Advertises server support for workspace folders and change notifications. |
| `Csls.Protocol.WorkspaceServerCapabilities` | Advertises server capabilities that apply to the complete workspace. |
| `Csls.Protocol.WorkspaceSymbol` | Describes one source declaration returned by workspace symbol search. |
| `Csls.Protocol.WorkspaceSymbolData` | Preserves immutable source coordinates required to resolve a workspace symbol. |
| `Csls.Protocol.WorkspaceSymbolLocation` | Identifies a workspace symbol document with an optional resolved range. |
| `Csls.Protocol.WorkspaceSymbolOptions` | Advertises workspace symbol search and lazy location resolution behavior. |
| `Csls.Protocol.WorkspaceSymbolParams` | Carries the client pattern used to search workspace declarations. |

## Csls.Control.Contracts

| Type | Description |
| --- | --- |
| `Csls.Control.Contracts.ControlApplyEditPlanRequest` | Identifies one previously previewed edit plan for explicit application. |
| `Csls.Control.Contracts.ControlApplyEditPlanResult` | Reports the new workspace generation and documents changed by an applied edit plan. |
| `Csls.Control.Contracts.ControlBuildHostInfo` | Describes one Roslyn workspace host exposed by the control protocol. |
| `Csls.Control.Contracts.ControlCacheInfo` | Describes one bounded session cache exposed by the control protocol. |
| `Csls.Control.Contracts.ControlCancelRequest` | Identifies one live request to cancel through the control protocol. |
| `Csls.Control.Contracts.ControlCancelRequestResult` | Describes the result of one request cancellation attempt. |
| `Csls.Control.Contracts.ControlCodeActionPlan` | Exposes one concrete code action and its optional one-use application plan. |
| `Csls.Control.Contracts.ControlCodeActionRequest` | Identifies one source range and the code-action categories to preview. |
| `Csls.Control.Contracts.ControlCompletionRequest` | Identifies one document and UTF-16 position for a control completion request. |
| `Csls.Control.Contracts.ControlConnectionInfo` | Describes the negotiated lifetime settings for one control connection. |
| `Csls.Control.Contracts.ControlDashboardRequest` | Selects optional expensive data for one bounded dashboard snapshot. |
| `Csls.Control.Contracts.ControlDashboardSnapshot` | Describes one bounded dashboard observation of a live csls session. |
| `Csls.Control.Contracts.ControlDiagnosticInfo` | Describes one compiler or analyzer diagnostic exposed by the control protocol. |
| `Csls.Control.Contracts.ControlDiagnosticRequest` | Identifies one document and optional prior result for a control diagnostic request. |
| `Csls.Control.Contracts.ControlDocumentInfo` | Describes one loaded source document exposed by the control protocol. |
| `Csls.Control.Contracts.ControlDocumentRequest` | Identifies one absolute source document for a control operation. |
| `Csls.Control.Contracts.ControlEditPlan` | Exposes a bounded one-use edit plan and every precondition required to apply it. |
| `Csls.Control.Contracts.ControlFormattingRequest` | Identifies one source document and the preferences for formatting preview. |
| `Csls.Control.Contracts.ControlHoverRequest` | Identifies an absolute document path and UTF-16 position for a control hover request. |
| `Csls.Control.Contracts.ControlHoverResult` | Reports whether a control hover request resolved language information. |
| `Csls.Control.Contracts.ControlJson` | Creates source-generated serializer options shared by control and MCP boundaries. |
| `Csls.Control.Contracts.ControlJsonSerializerContext` | Provides generated System.Text.Json metadata for the versioned control protocol. |
| `Csls.Control.Contracts.ControlLogEntry` | Describes one bounded structured worker log entry exposed by the control protocol. |
| `Csls.Control.Contracts.ControlMethods` | Defines the versioned StreamJsonRpc method names for the csls control protocol. |
| `Csls.Control.Contracts.ControlNavigationRequest` | Identifies one document position and declaration behavior for source navigation. |
| `Csls.Control.Contracts.ControlProjectInfo` | Describes one loaded Roslyn project exposed by the control protocol. |
| `Csls.Control.Contracts.ControlProtocol` | Defines shared constants for the versioned csls control protocol. |
| `Csls.Control.Contracts.ControlRenameRequest` | Identifies one source symbol and the replacement identifier for rename preview. |
| `Csls.Control.Contracts.ControlRequestInfo` | Describes one queued or running request exposed by the control protocol. |
| `Csls.Control.Contracts.ControlRequestSchedulerInfo` | Describes bounded request and queue activity exposed by the control protocol. |
| `Csls.Control.Contracts.ControlResourcePrecondition` | Describes the exact resource state required before a control edit plan is applied. |
| `Csls.Control.Contracts.ControlSelectionRangeRequest` | Identifies one document and ordered positions for syntax selection hierarchies. |
| `Csls.Control.Contracts.ControlSessionInfo` | Describes a live csls language-server session exposed through the control socket. |
| `Csls.Control.Contracts.ControlSignatureHelpRequest` | Identifies one absolute document position for signature help. |
| `Csls.Control.Contracts.ControlTraceEntry` | Describes one bounded request lifecycle record in a control trace. |
| `Csls.Control.Contracts.ControlTraceInfo` | Describes the active or most recently stopped bounded request trace. |
| `Csls.Control.Contracts.ControlWorkspaceInfo` | Describes one loaded workspace folder exposed by the control protocol. |
| `Csls.Control.Contracts.ControlWorkspaceOperationResult` | Describes one completed workspace operation returned through the control protocol. |
| `Csls.Control.Contracts.ControlWorkspaceSymbolRequest` | Carries a bounded workspace declaration search pattern. |
| `Csls.Control.Contracts.IControlRpcTarget` | Defines the explicitly registered methods implemented by a csls control session. |

## Csls.Debugger.Contracts

| Type | Description |
| --- | --- |
| `Csls.Debugger.Contracts.DebugAssignmentResult` | Describes one completed managed assignment and its resulting stop generation. |
| `Csls.Debugger.Contracts.DebugAttachRequest` | Selects one running CoreCLR process for debugger attachment. |
| `Csls.Debugger.Contracts.DebugBreakpointLocation` | Describes one executable source location reported by loaded symbols. |
| `Csls.Debugger.Contracts.DebugBreakpointSnapshot` | Describes every authoritative breakpoint configured in one debugger session. |
| `Csls.Debugger.Contracts.DebugCompletionInfo` | Describes one generation-bound debugger expression completion. |
| `Csls.Debugger.Contracts.DebugCompletionItemKind` | Identifies the debugger-facing category of one expression completion. |
| `Csls.Debugger.Contracts.DebugDisassembly` | Contains an exact-count managed-IL disassembly response. |
| `Csls.Debugger.Contracts.DebugDisassemblyRequest` | Selects a bounded managed-IL instruction range from a stopped frame. |
| `Csls.Debugger.Contracts.DebugDumpOpenRequest` | Selects a managed process dump and one runtime for read-only inspection. |
| `Csls.Debugger.Contracts.DebugEvaluateRequest` | Selects a stopped managed frame and expression for evaluation. |
| `Csls.Debugger.Contracts.DebugEvaluateResult` | Describes one expression result from a stopped managed frame. |
| `Csls.Debugger.Contracts.DebugExceptionBreakMode` | Identifies the managed exception stage that caused a debugger stop. |
| `Csls.Debugger.Contracts.DebugExceptionBreakpointRequest` | Describes one managed exception-stage and type filter. |
| `Csls.Debugger.Contracts.DebugExceptionBreakpointSetRequest` | Replaces the complete managed exception breakpoint policy. |
| `Csls.Debugger.Contracts.DebugExceptionInfo` | Describes the managed exception responsible for the current debugger stop. |
| `Csls.Debugger.Contracts.DebugExceptionInfoRequest` | Selects the managed thread that caused the current exception stop. |
| `Csls.Debugger.Contracts.DebugExecuteExpressionRequest` | Selects a stopped managed frame and expression for authorized target execution. |
| `Csls.Debugger.Contracts.DebugExpressionCompileRequest` | Selects a source-language grammar and expression for evaluator binding. |
| `Csls.Debugger.Contracts.DebugExpressionLanguage` | Identifies the source-language grammar used to bind a debugger expression. |
| `Csls.Debugger.Contracts.DebugExpressionNode` | Carries one immutable operation in a language-neutral debugger expression tree. |
| `Csls.Debugger.Contracts.DebugExpressionNodeKind` | Identifies one operation in a language-neutral debugger expression tree. |
| `Csls.Debugger.Contracts.DebugExpressionOperator` | Identifies one language-neutral debugger expression operator. |
| `Csls.Debugger.Contracts.DebugExpressionPlan` | Carries one versioned language-neutral debugger expression plan. |
| `Csls.Debugger.Contracts.DebugFunctionBreakpointInfo` | Describes the current binding state of one managed function breakpoint. |
| `Csls.Debugger.Contracts.DebugFunctionBreakpointRequest` | Describes one requested managed function breakpoint before runtime binding. |
| `Csls.Debugger.Contracts.DebugFunctionBreakpointSetRequest` | Carries the complete replacement set of managed function breakpoints. |
| `Csls.Debugger.Contracts.DebugGotoRequest` | Selects a managed thread and runtime-approved instruction-pointer destination. |
| `Csls.Debugger.Contracts.DebugGotoTargetInfo` | Describes one runtime-approved destination for moving the current instruction pointer. |
| `Csls.Debugger.Contracts.DebugGotoTargetsRequest` | Selects a current frame and source position for safe instruction-pointer destinations. |
| `Csls.Debugger.Contracts.DebugHotReloadActiveStatement` | Maps one active old method instruction to its updated source span. |
| `Csls.Debugger.Contracts.DebugHotReloadRequest` | Carries one compiler-produced managed Hot Reload module update. |
| `Csls.Debugger.Contracts.DebugHotReloadResult` | Describes one successfully applied managed Hot Reload module update. |
| `Csls.Debugger.Contracts.DebugInstructionBreakpointInfo` | Describes the current binding state of one managed-IL instruction breakpoint. |
| `Csls.Debugger.Contracts.DebugInstructionBreakpointRequest` | Describes one requested managed-IL instruction breakpoint. |
| `Csls.Debugger.Contracts.DebugInstructionBreakpointSetRequest` | Replaces every managed-IL instruction breakpoint in one debugger session. |
| `Csls.Debugger.Contracts.DebugInstructionInfo` | Describes one managed IL instruction or an out-of-range placeholder. |
| `Csls.Debugger.Contracts.DebugLaunchRequest` | Describes one debugger-owned managed target launch. |
| `Csls.Debugger.Contracts.DebugMemoryReadRequest` | Selects a bounded memory range relative to an opaque stopped-state handle. |
| `Csls.Debugger.Contracts.DebugMemoryReadResult` | Contains one bounded target-memory read and its first resolved address. |
| `Csls.Debugger.Contracts.DebugModuleInfo` | Describes one managed module loaded in the debug target. |
| `Csls.Debugger.Contracts.DebugModulePage` | Carries a page of loaded managed modules and the complete module count. |
| `Csls.Debugger.Contracts.DebugModuleSymbolKind` | Identifies the validated symbol format loaded for a managed module. |
| `Csls.Debugger.Contracts.DebugModulesRequest` | Selects a bounded page of managed modules. |
| `Csls.Debugger.Contracts.DebugOutputCategory` | Identifies a debugger or target output channel. |
| `Csls.Debugger.Contracts.DebugOutputEntry` | Describes one bounded target-output segment retained by debugger control. |
| `Csls.Debugger.Contracts.DebugOutputPage` | Contains one bounded page from the retained debugger-output stream. |
| `Csls.Debugger.Contracts.DebugOutputRequest` | Selects a bounded target-output page after a stable sequence cursor. |
| `Csls.Debugger.Contracts.DebugScopeInfo` | Describes one debugger variable scope at a stopped frame. |
| `Csls.Debugger.Contracts.DebugScopesRequest` | Selects the scopes owned by one current-generation frame. |
| `Csls.Debugger.Contracts.DebugSessionSnapshot` | Describes the current observable state of one debugger target session. |
| `Csls.Debugger.Contracts.DebugSessionState` | Identifies the lifecycle state of a debugger session. |
| `Csls.Debugger.Contracts.DebugSetExpressionRequest` | Selects one writable source expression for an exact stopped-generation assignment. |
| `Csls.Debugger.Contracts.DebugSetVariableRequest` | Selects one variable-container child for an exact stopped-generation assignment. |
| `Csls.Debugger.Contracts.DebugSourceBreakpointInfo` | Describes the current binding state of one source breakpoint. |
| `Csls.Debugger.Contracts.DebugSourceBreakpointRequest` | Describes one requested source breakpoint before runtime binding. |
| `Csls.Debugger.Contracts.DebugSourceBreakpointSetRequest` | Replaces the complete source-breakpoint set for one document. |
| `Csls.Debugger.Contracts.DebugSourceChecksum` | Describes a Portable PDB source-document checksum. |
| `Csls.Debugger.Contracts.DebugSourceContent` | Carries source text resolved through a session-local source reference. |
| `Csls.Debugger.Contracts.DebugSourceInfo` | Describes one source document represented by loaded debugger symbols. |
| `Csls.Debugger.Contracts.DebugSourceRequest` | Selects retrievable source content by its session-local reference. |
| `Csls.Debugger.Contracts.DebugStackFrameInfo` | Describes one managed stack frame at a specific stop generation. |
| `Csls.Debugger.Contracts.DebugStackRequest` | Selects a bounded page of frames from one managed thread. |
| `Csls.Debugger.Contracts.DebugStackTrace` | Carries a paged managed stack and its exact total when the stack end has been observed. |
| `Csls.Debugger.Contracts.DebugStepKind` | Identifies a source-level managed stepping operation. |
| `Csls.Debugger.Contracts.DebugStepRequest` | Selects one managed thread and source-level stepping operation. |
| `Csls.Debugger.Contracts.DebugStepTargetInfo` | Describes one generation-bound call that can be selected for Step Into. |
| `Csls.Debugger.Contracts.DebugStepTargetsRequest` | Selects one generation-bound active frame for source-aware Step Into discovery. |
| `Csls.Debugger.Contracts.DebugStopGeneration` | Identifies the target stop that owns debugger inspection handles. |
| `Csls.Debugger.Contracts.DebugSymbolModuleFilterMode` | Selects whether symbol loading uses an exclusion or inclusion list. |
| `Csls.Debugger.Contracts.DebugSymbolModuleFilterOptions` | Filters eager symbol lookup by managed module file name. |
| `Csls.Debugger.Contracts.DebugSymbolOptions` | Configures trusted locations used to resolve matching debugger symbols. |
| `Csls.Debugger.Contracts.DebugThreadInfo` | Describes one managed thread visible at a debugger stop. |
| `Csls.Debugger.Contracts.DebugVariableFilter` | Selects immediate variable children by their container-defined indexing category. |
| `Csls.Debugger.Contracts.DebugVariableInfo` | Describes one debugger variable and its immediate formatted value. |
| `Csls.Debugger.Contracts.DebugVariablePresentationKind` | Identifies how a debugger client should present one variable entry. |
| `Csls.Debugger.Contracts.DebugVariablesRequest` | Selects a bounded page from one current-generation variable container. |
| `Csls.Debugger.Contracts.DebuggerControlMethods` | Names the versioned private debugger control methods. |
| `Csls.Debugger.Contracts.DebuggerControlNotifications` | Defines private debugger-control notification method names. |
| `Csls.Debugger.Contracts.DebuggerControlProtocol` | Defines the private debugger control protocol version. |
| `Csls.Debugger.Contracts.DebuggerEvaluatorMethods` | Names the versioned private managed evaluator methods. |
| `Csls.Debugger.Contracts.DebuggerEvaluatorProtocol` | Defines the private managed evaluator protocol version. |
| `Csls.Debugger.Contracts.DebuggerResourceChangeEventArgs` | Signals that authoritative debugger resources should be read again. |
| `Csls.Debugger.Contracts.DebuggerResourceChangeKind` | Identifies debugger resources invalidated by one engine notification. |
| `Csls.Debugger.Contracts.IDebuggerControlTarget` | Defines read-only breakpoint inspection exposed through private debugger control RPC. |
| `Csls.Debugger.Contracts.IDebuggerEvaluatorTarget` | Defines the operations exposed by one supervised managed evaluator worker. |
