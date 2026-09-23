using Csls.Control.Contracts;
using Csls.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using static Csls.Mcp.Worker.McpDocumentValidation;
using LspRange = Csls.Protocol.Range;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes semantic edit previews and application for explicitly selected csls sessions.
/// </summary>
[McpServerToolType]
internal sealed class CslsMcpEditTools
{
    private readonly McpSessionBroker _sessionBroker;

    /// <summary>
    /// Creates MCP tools backed by the shared MCP session broker.
    /// </summary>
    /// <param name="sessionBroker">The shared selector-aware session broker.</param>
    public CslsMcpEditTools(McpSessionBroker sessionBroker)
    {
        ArgumentNullException.ThrowIfNull(sessionBroker);
        _sessionBroker = sessionBroker;
    }

    /// <summary>
    /// Previews a semantic cross-document rename as version-aware workspace edits.
    /// </summary>
    /// <param name="documentPath">The absolute path of the target document.</param>
    /// <param name="line">The zero-based document line.</param>
    /// <param name="character">The zero-based UTF-16 character offset.</param>
    /// <param name="newName">The requested replacement identifier.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The complete version-aware workspace edit preview.</returns>
    [McpServerTool(
        Name = "preview_rename",
        Title = "Preview C# rename",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Preview a semantic C# rename as version-aware workspace edits.")]
    public Task<ControlEditPlan> PreviewRenameAsync(
        [Description("Absolute path of the document loaded by the selected csls session.")]
        string documentPath,
        [Description("Zero-based document line.")]
        int line,
        [Description("Zero-based UTF-16 character offset.")]
        int character,
        [Description("Valid replacement C# identifier.")]
        string newName,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        ValidateDocumentPosition(documentPath, line, character);
        if (string.IsNullOrWhiteSpace(newName) || newName.Length > 512)
        {
            throw new McpException("newName must contain between 1 and 512 characters.");
        }

        return controlClient.PreviewRenameAsync(
            new ControlRenameRequest
            {
                DocumentPath = Path.GetFullPath(documentPath),
                Position = new Position(line, character),
                NewName = newName
            },
            cancellationToken);
    }

    /// <summary>
    /// Previews Roslyn formatting edits for the selected document.
    /// </summary>
    /// <param name="documentPath">The absolute path of the target document.</param>
    /// <param name="tabSize">The visual indentation width.</param>
    /// <param name="insertSpaces">Whether indentation uses spaces.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The bounded non-overlapping formatting edit preview.</returns>
    [McpServerTool(
        Name = "preview_formatting",
        Title = "Preview C# formatting",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Preview complete-document Roslyn formatting edits.")]
    public Task<ControlEditPlan> PreviewFormattingAsync(
        [Description("Absolute path of the document loaded by the selected csls session.")]
        string documentPath,
        [Description("Indentation width from 1 through 32.")]
        int tabSize,
        [Description("Use spaces for indentation.")]
        bool insertSpaces,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        ValidateDocumentPath(documentPath);
        if (tabSize is < 1 or > 32)
        {
            throw new McpException("tabSize must be between 1 and 32.");
        }

        return controlClient.PreviewFormattingAsync(
            new ControlFormattingRequest
            {
                DocumentPath = Path.GetFullPath(documentPath),
                Options = new FormattingOptions
                {
                    TabSize = tabSize,
                    InsertSpaces = insertSpaces,
                    TrimTrailingWhitespace = true,
                    InsertFinalNewline = true,
                    TrimFinalNewlines = true
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Explicitly applies one unexpired edit plan after every precondition passes.
    /// </summary>
    /// <param name="planId">The unguessable one-use plan identifier.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The new generation and changed document paths.</returns>
    [McpServerTool(
        Name = "apply_edit_plan",
        Title = "Apply csls edit plan",
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        ReadOnly = false,
        UseStructuredContent = true)]
    [Description("Apply one previewed csls edit plan after generation, version, and SHA-256 checks pass.")]
    public Task<ControlApplyEditPlanResult> ApplyEditPlanAsync(
        [Description("One-use plan identifier returned by a preview tool.")]
        string planId,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        if (!Guid.TryParse(planId, out Guid parsedPlanId))
        {
            throw new McpException("planId must be a valid GUID.");
        }

        return controlClient.ApplyEditPlanAsync(
            new ControlApplyEditPlanRequest { PlanId = parsedPlanId },
            cancellationToken);
    }

    /// <summary>
    /// Gets concrete Roslyn code actions for one document range.
    /// </summary>
    /// <param name="documentPath">The absolute path of the target document.</param>
    /// <param name="startLine">The zero-based start line.</param>
    /// <param name="startCharacter">The zero-based UTF-16 start character.</param>
    /// <param name="endLine">The zero-based end line.</param>
    /// <param name="endCharacter">The zero-based UTF-16 end character.</param>
    /// <param name="kind">The optional hierarchical code-action category.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The supported code actions and optional one-use edit plans.</returns>
    [McpServerTool(
        Name = "get_code_actions",
        Title = "Get C# code actions",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Get concrete Roslyn code actions for a zero-based UTF-16 source range.")]
    public Task<IReadOnlyList<ControlCodeActionPlan>> GetCodeActionsAsync(
        [Description("Absolute path of the document loaded by the selected csls session.")]
        string documentPath,
        [Description("Zero-based start line.")]
        int startLine,
        [Description("Zero-based UTF-16 start character.")]
        int startCharacter,
        [Description("Zero-based end line.")]
        int endLine,
        [Description("Zero-based UTF-16 end character.")]
        int endCharacter,
        [Description("Optional action category such as source or source.organizeImports.")]
        string? kind,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        var controlClient = new McpTargetClient(
            _sessionBroker,
            workspace,
            session,
            socket);
        ValidateDocumentPosition(documentPath, startLine, startCharacter);
        ValidateDocumentPosition(documentPath, endLine, endCharacter);
        if (endLine < startLine ||
            (endLine == startLine && endCharacter < startCharacter))
        {
            throw new McpException("The code-action range end must not precede its start.");
        }

        if (kind is { Length: > 256 })
        {
            throw new McpException("kind cannot exceed 256 characters.");
        }

        return controlClient.GetCodeActionsAsync(
            new ControlCodeActionRequest
            {
                DocumentPath = Path.GetFullPath(documentPath),
                Range = new LspRange(
                    new Position(startLine, startCharacter),
                    new Position(endLine, endCharacter)),
                Only = string.IsNullOrWhiteSpace(kind) ? null : [kind]
            },
            cancellationToken);
    }
}
