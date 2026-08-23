using Csls.Control;
using Csls.Control.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes live csls session state as discoverable MCP resources.
/// </summary>
[McpServerResourceType]
internal sealed class CslsMcpResources
{
    private const string SessionUri = "csls://session/current";
    private readonly ControlRpcClient _controlClient;

    /// <summary>
    /// Creates MCP resources backed by the shared versioned csls control client.
    /// </summary>
    /// <param name="controlClient">The attached session control client.</param>
    public CslsMcpResources(ControlRpcClient controlClient)
    {
        ArgumentNullException.ThrowIfNull(controlClient);
        _controlClient = controlClient;
    }

    /// <summary>
    /// Reads the current csls session as source-generated JSON.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The current session resource contents.</returns>
    [McpServerResource(
        UriTemplate = SessionUri,
        Name = "Current csls session",
        MimeType = "application/json")]
    [Description("Current lifecycle, workspace generation, roots, and process details for the attached csls session.")]
    public async Task<TextResourceContents> GetSessionAsync(
        CancellationToken cancellationToken)
    {
        ControlSessionInfo session = await _controlClient
            .GetSessionAsync(cancellationToken)
            .ConfigureAwait(false);
        return new TextResourceContents
        {
            Uri = SessionUri,
            MimeType = "application/json",
            Text = JsonSerializer.Serialize(
                session,
                ControlJsonSerializerContext.Default.ControlSessionInfo)
        };
    }
}
