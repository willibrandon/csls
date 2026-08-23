using System.ComponentModel;
using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes bounded language-intelligence operations from an attached csls control session.
/// </summary>
[McpServerToolType]
internal sealed class CslsMcpTools
{
    private const int MaximumPathLength = 4096;
    private readonly ControlRpcClient _controlClient;

    /// <summary>
    /// Creates MCP tools backed by the shared versioned csls control client.
    /// </summary>
    /// <param name="controlClient">The attached session control client.</param>
    public CslsMcpTools(ControlRpcClient controlClient)
    {
        ArgumentNullException.ThrowIfNull(controlClient);
        _controlClient = controlClient;
    }

    /// <summary>
    /// Gets lifecycle and workspace information for the attached csls session.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The current attached session information.</returns>
    [McpServerTool(
        Name = "get_session",
        Title = "Get csls session",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Get lifecycle, workspace generation, roots, and process details for the attached csls session.")]
    public Task<ControlSessionInfo> GetSessionAsync(CancellationToken cancellationToken) =>
        _controlClient.GetSessionAsync(cancellationToken);

    /// <summary>
    /// Gets Roslyn hover information for an exact document position in the attached session.
    /// </summary>
    /// <param name="documentPath">The absolute path of an open document.</param>
    /// <param name="line">The zero-based document line.</param>
    /// <param name="character">The zero-based UTF-16 character offset.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The optional hover result and exact source range.</returns>
    [McpServerTool(
        Name = "get_hover",
        Title = "Get C# hover",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Resolve C# signature and documentation hover information at a zero-based UTF-16 document position.")]
    public Task<ControlHoverResult> GetHoverAsync(
        [Description("Absolute path of the document loaded by the attached csls session.")]
        string documentPath,
        [Description("Zero-based document line.")]
        int line,
        [Description("Zero-based UTF-16 character offset.")]
        int character,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(documentPath) ||
            documentPath.Length > MaximumPathLength)
        {
            throw new McpException(
                $"documentPath must contain between 1 and {MaximumPathLength} characters.");
        }

        if (line < 0)
        {
            throw new McpException("line must be zero or greater.");
        }

        if (character < 0)
        {
            throw new McpException("character must be zero or greater.");
        }

        return _controlClient.GetHoverAsync(
            new ControlHoverRequest
            {
                DocumentPath = Path.GetFullPath(documentPath),
                Position = new Position(line, character)
            },
            cancellationToken);
    }

    /// <summary>
    /// Gets compiler and analyzer diagnostics for one document in the attached session.
    /// </summary>
    /// <param name="documentPath">The absolute path of an open document.</param>
    /// <param name="previousResultId">The optional prior opaque diagnostic result identifier.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>A complete or unchanged document diagnostic report.</returns>
    [McpServerTool(
        Name = "get_diagnostics",
        Title = "Get C# diagnostics",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Get current compiler and analyzer diagnostics for one document in the attached csls session.")]
    public Task<DocumentDiagnosticReport> GetDiagnosticsAsync(
        [Description("Absolute path of the document loaded by the attached csls session.")]
        string documentPath,
        CancellationToken cancellationToken,
        [Description("Optional resultId from a prior diagnostic response.")]
        string? previousResultId = null)
    {
        if (string.IsNullOrWhiteSpace(documentPath) ||
            documentPath.Length > MaximumPathLength)
        {
            throw new McpException(
                $"documentPath must contain between 1 and {MaximumPathLength} characters.");
        }

        if (previousResultId is { Length: > 256 })
        {
            throw new McpException("previousResultId cannot exceed 256 characters.");
        }

        return _controlClient.GetDiagnosticsAsync(
            new ControlDiagnosticRequest
            {
                DocumentPath = Path.GetFullPath(documentPath),
                PreviousResultId = previousResultId
            },
            cancellationToken);
    }

    /// <summary>
    /// Gets bounded Roslyn completion candidates at one document position.
    /// </summary>
    /// <param name="documentPath">The absolute path of an open document.</param>
    /// <param name="line">The zero-based document line.</param>
    /// <param name="character">The zero-based UTF-16 character offset.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The ordered completion list and exact commit edits.</returns>
    [McpServerTool(
        Name = "get_completion",
        Title = "Get C# completion",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Get bounded C# completion candidates and exact commit edits at a zero-based UTF-16 position.")]
    public Task<CompletionList> GetCompletionAsync(
        [Description("Absolute path of the document loaded by the attached csls session.")]
        string documentPath,
        [Description("Zero-based document line.")]
        int line,
        [Description("Zero-based UTF-16 character offset.")]
        int character,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(documentPath) ||
            documentPath.Length > MaximumPathLength)
        {
            throw new McpException(
                $"documentPath must contain between 1 and {MaximumPathLength} characters.");
        }

        if (line < 0)
        {
            throw new McpException("line must be zero or greater.");
        }

        if (character < 0)
        {
            throw new McpException("character must be zero or greater.");
        }

        return _controlClient.GetCompletionAsync(
            new ControlCompletionRequest
            {
                DocumentPath = Path.GetFullPath(documentPath),
                Position = new Position(line, character)
            },
            cancellationToken);
    }

    /// <summary>
    /// Gets source definitions for the symbol at one document position.
    /// </summary>
    /// <param name="documentPath">The absolute path of an open document.</param>
    /// <param name="line">The zero-based document line.</param>
    /// <param name="character">The zero-based UTF-16 character offset.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The bounded source definition locations.</returns>
    [McpServerTool(
        Name = "get_definition",
        Title = "Get C# definition",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Find source definitions for the C# symbol at a zero-based UTF-16 document position.")]
    public Task<IReadOnlyList<Location>> GetDefinitionAsync(
        [Description("Absolute path of the document loaded by the attached csls session.")]
        string documentPath,
        [Description("Zero-based document line.")]
        int line,
        [Description("Zero-based UTF-16 character offset.")]
        int character,
        CancellationToken cancellationToken)
    {
        ControlNavigationRequest request = CreateNavigationRequest(
            documentPath,
            line,
            character,
            includeDeclaration: false);
        return _controlClient.GetDefinitionAsync(request, cancellationToken);
    }

    /// <summary>
    /// Gets source references for the symbol at one document position.
    /// </summary>
    /// <param name="documentPath">The absolute path of an open document.</param>
    /// <param name="line">The zero-based document line.</param>
    /// <param name="character">The zero-based UTF-16 character offset.</param>
    /// <param name="includeDeclaration">Whether declaration locations are included.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The bounded source reference locations.</returns>
    [McpServerTool(
        Name = "get_references",
        Title = "Get C# references",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Find source references for the C# symbol at a zero-based UTF-16 document position.")]
    public Task<IReadOnlyList<Location>> GetReferencesAsync(
        [Description("Absolute path of the document loaded by the attached csls session.")]
        string documentPath,
        [Description("Zero-based document line.")]
        int line,
        [Description("Zero-based UTF-16 character offset.")]
        int character,
        [Description("Include source declarations in the result.")]
        bool includeDeclaration,
        CancellationToken cancellationToken)
    {
        ControlNavigationRequest request = CreateNavigationRequest(
            documentPath,
            line,
            character,
            includeDeclaration);
        return _controlClient.GetReferencesAsync(request, cancellationToken);
    }

    private static ControlNavigationRequest CreateNavigationRequest(
        string documentPath,
        int line,
        int character,
        bool includeDeclaration)
    {
        if (string.IsNullOrWhiteSpace(documentPath) ||
            documentPath.Length > MaximumPathLength)
        {
            throw new McpException(
                $"documentPath must contain between 1 and {MaximumPathLength} characters.");
        }

        if (line < 0)
        {
            throw new McpException("line must be zero or greater.");
        }

        if (character < 0)
        {
            throw new McpException("character must be zero or greater.");
        }

        return new ControlNavigationRequest
        {
            DocumentPath = Path.GetFullPath(documentPath),
            Position = new Position(line, character),
            IncludeDeclaration = includeDeclaration
        };
    }
}
