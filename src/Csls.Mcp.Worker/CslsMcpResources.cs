using Csls.Control.Contracts;
using Csls.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes selected live csls session state as discoverable MCP resource templates.
/// </summary>
[McpServerResourceType]
internal sealed class CslsMcpResources
{
    private const int MaximumPathLength = 4096;
    private const string SessionUriTemplate = "csls://session/{?workspace,session,socket}";
    private const string WorkspaceUriTemplate = "csls://workspace/{?workspace,session,socket}";
    private const string ProjectUriTemplate = "csls://project/{?workspace,session,socket,path}";
    private const string DocumentUriTemplate = "csls://document/{?workspace,session,socket,path}";
    private const string DiagnosticUriTemplate = "csls://diagnostic/{?workspace,session,socket,path}";
    private readonly McpSessionBroker _sessionBroker;

    /// <summary>
    /// Creates MCP resources backed by the shared MCP session broker.
    /// </summary>
    /// <param name="sessionBroker">The shared selector-aware session broker.</param>
    public CslsMcpResources(McpSessionBroker sessionBroker)
    {
        ArgumentNullException.ThrowIfNull(sessionBroker);
        _sessionBroker = sessionBroker;
    }

    /// <summary>
    /// Reads one selected csls session as source-generated JSON.
    /// </summary>
    /// <param name="requestContext">The MCP resource request context.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The selected session resource contents.</returns>
    [McpServerResource(
        UriTemplate = SessionUriTemplate,
        Name = "csls session",
        MimeType = "application/json")]
    [Description("Lifecycle, workspace generation, roots, and process details for one selected csls session.")]
    public Task<TextResourceContents> GetSessionAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        string? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null) =>
        _sessionBroker.InvokeAsync(
            workspace,
            ParseSession(session),
            socket,
            async (client, requestToken) =>
            {
                ControlSessionInfo selected = await client.GetSessionAsync(requestToken)
                    .ConfigureAwait(false);
                return CreateJsonResource(
                    requestContext.Params.Uri,
                    JsonSerializer.Serialize(
                        selected,
                        ControlJsonSerializerContext.Default.ControlSessionInfo));
            },
            cancellationToken);

    /// <summary>
    /// Reads one selected bounded workspace state as source-generated JSON.
    /// </summary>
    /// <param name="requestContext">The MCP resource request context.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The selected workspace resource contents.</returns>
    [McpServerResource(
        UriTemplate = WorkspaceUriTemplate,
        Name = "csls workspace",
        MimeType = "application/json")]
    [Description("Workspaces, projects, documents, requests, hosts, caches, and logs for one selected session.")]
    public Task<TextResourceContents> GetWorkspaceAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        string? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null) =>
        _sessionBroker.InvokeAsync(
            workspace,
            ParseSession(session),
            socket,
            async (client, requestToken) =>
            {
                ControlDashboardSnapshot snapshot = await client.GetDashboardSnapshotAsync(
                    new ControlDashboardRequest(),
                    requestToken).ConfigureAwait(false);
                return CreateJsonResource(
                    requestContext.Params.Uri,
                    JsonSerializer.Serialize(
                        snapshot,
                        ControlJsonSerializerContext.Default.ControlDashboardSnapshot));
            },
            cancellationToken);

    /// <summary>
    /// Reads one loaded Roslyn project from a selected session.
    /// </summary>
    /// <param name="requestContext">The MCP resource request context.</param>
    /// <param name="path">The absolute project file path.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The selected project resource contents.</returns>
    [McpServerResource(
        UriTemplate = ProjectUriTemplate,
        Name = "csls project",
        MimeType = "application/json")]
    [Description("One loaded Roslyn project selected by target and absolute project file path.")]
    public Task<TextResourceContents> GetProjectAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        CancellationToken cancellationToken,
        [Description("Absolute loaded project file path.")]
        string? path = null,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        string? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        string fullPath = ValidatePath(path);
        return _sessionBroker.InvokeAsync(
            workspace,
            ParseSession(session),
            socket,
            async (client, requestToken) =>
            {
                ControlDashboardSnapshot snapshot = await client.GetDashboardSnapshotAsync(
                    new ControlDashboardRequest(),
                    requestToken).ConfigureAwait(false);
                ControlProjectInfo project = snapshot.Projects.FirstOrDefault(project =>
                    project.FilePath is not null && PathsEqual(project.FilePath, fullPath))
                    ?? throw new McpException($"No loaded csls project has path {fullPath}.");
                return CreateJsonResource(
                    requestContext.Params.Uri,
                    JsonSerializer.Serialize(
                        project,
                        ControlJsonSerializerContext.Default.ControlProjectInfo));
            },
            cancellationToken);
    }

    /// <summary>
    /// Reads one loaded source document from a selected session.
    /// </summary>
    /// <param name="requestContext">The MCP resource request context.</param>
    /// <param name="path">The absolute source document path.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The selected document resource contents.</returns>
    [McpServerResource(
        UriTemplate = DocumentUriTemplate,
        Name = "csls document",
        MimeType = "application/json")]
    [Description("One loaded C# document selected by target and absolute source file path.")]
    public Task<TextResourceContents> GetDocumentAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        CancellationToken cancellationToken,
        [Description("Absolute loaded source document path.")]
        string? path = null,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        string? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        string fullPath = ValidatePath(path);
        return _sessionBroker.InvokeAsync(
            workspace,
            ParseSession(session),
            socket,
            async (client, requestToken) =>
            {
                ControlDashboardSnapshot snapshot = await client.GetDashboardSnapshotAsync(
                    new ControlDashboardRequest(),
                    requestToken).ConfigureAwait(false);
                ControlDocumentInfo document = snapshot.Documents.FirstOrDefault(document =>
                    document.FilePath is not null && PathsEqual(document.FilePath, fullPath))
                    ?? throw new McpException($"No loaded csls document has path {fullPath}.");
                return CreateJsonResource(
                    requestContext.Params.Uri,
                    JsonSerializer.Serialize(
                        document,
                        ControlJsonSerializerContext.Default.ControlDocumentInfo));
            },
            cancellationToken);
    }

    /// <summary>
    /// Reads current diagnostics for one document from a selected session.
    /// </summary>
    /// <param name="requestContext">The MCP resource request context.</param>
    /// <param name="path">The absolute source document path.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The current document diagnostic resource contents.</returns>
    [McpServerResource(
        UriTemplate = DiagnosticUriTemplate,
        Name = "csls document diagnostics",
        MimeType = "application/json")]
    [Description("Current compiler and analyzer diagnostics for one selected loaded C# document.")]
    public Task<TextResourceContents> GetDiagnosticsAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        CancellationToken cancellationToken,
        [Description("Absolute loaded source document path.")]
        string? path = null,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        string? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        string fullPath = ValidatePath(path);
        return _sessionBroker.InvokeAsync(
            workspace,
            ParseSession(session),
            socket,
            async (client, requestToken) =>
            {
                DocumentDiagnosticReport diagnostics = await client.GetDiagnosticsAsync(
                    new ControlDiagnosticRequest { DocumentPath = fullPath },
                    requestToken).ConfigureAwait(false);
                return CreateJsonResource(
                    requestContext.Params.Uri,
                    JsonSerializer.Serialize(
                        diagnostics,
                        ControlJsonSerializerContext.Default.DocumentDiagnosticReport));
            },
            cancellationToken);
    }

    private static int? ParseSession(string? session)
    {
        if (session is null)
        {
            return null;
        }

        if (!int.TryParse(
                session,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int processId) || processId <= 0)
        {
            throw new McpException("session must be a positive process identifier.");
        }

        return processId;
    }

    private static TextResourceContents CreateJsonResource(string uri, string text) =>
        new()
        {
            Uri = uri,
            MimeType = "application/json",
            Text = text
        };

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static string ValidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathLength)
        {
            throw new McpException(
                $"path must contain between 1 and {MaximumPathLength} characters.");
        }

        return Path.GetFullPath(path);
    }
}
