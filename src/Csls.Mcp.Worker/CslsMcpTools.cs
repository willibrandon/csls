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
}
