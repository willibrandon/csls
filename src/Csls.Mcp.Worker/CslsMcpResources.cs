using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using ModelContextProtocol;
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
    private const int MaximumPathLength = 4096;
    private const string SessionUri = "csls://session/current";
    private const string WorkspaceUri = "csls://workspace/current";
    private const string ProjectUriTemplate = "csls://project{?path}";
    private const string DocumentUriTemplate = "csls://document{?path}";
    private const string DiagnosticUriTemplate = "csls://diagnostic{?path}";
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

    /// <summary>
    /// Reads the current bounded workspace state as source-generated JSON.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The current workspace resource contents.</returns>
    [McpServerResource(
        UriTemplate = WorkspaceUri,
        Name = "Current csls workspace",
        MimeType = "application/json")]
    [Description("Current workspaces, projects, documents, requests, build hosts, caches, and recent logs for the attached session.")]
    public async Task<TextResourceContents> GetWorkspaceAsync(
        CancellationToken cancellationToken)
    {
        ControlDashboardSnapshot snapshot = await _controlClient
            .GetDashboardSnapshotAsync(
                new ControlDashboardRequest(),
                cancellationToken).ConfigureAwait(false);
        return CreateJsonResource(
            WorkspaceUri,
            JsonSerializer.Serialize(
                snapshot,
                ControlJsonSerializerContext.Default.ControlDashboardSnapshot));
    }

    /// <summary>
    /// Reads one loaded Roslyn project selected by its absolute project path.
    /// </summary>
    /// <param name="requestContext">The MCP resource request context.</param>
    /// <param name="path">The absolute project file path.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The selected project resource contents.</returns>
    [McpServerResource(
        UriTemplate = ProjectUriTemplate,
        Name = "csls project",
        MimeType = "application/json")]
    [Description("One loaded Roslyn project selected by its absolute project file path.")]
    public async Task<TextResourceContents> GetProjectAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        [Description("Absolute loaded project file path.")]
        string path,
        CancellationToken cancellationToken)
    {
        string fullPath = ValidatePath(path);
        ControlDashboardSnapshot snapshot = await _controlClient
            .GetDashboardSnapshotAsync(
                new ControlDashboardRequest(),
                cancellationToken).ConfigureAwait(false);
        ControlProjectInfo project = snapshot.Projects.FirstOrDefault(project =>
            project.FilePath is not null && PathsEqual(project.FilePath, fullPath))
            ?? throw new McpException($"No loaded csls project has path {fullPath}.");
        return CreateJsonResource(
            requestContext.Params.Uri,
            JsonSerializer.Serialize(
                project,
                ControlJsonSerializerContext.Default.ControlProjectInfo));
    }

    /// <summary>
    /// Reads one loaded source document selected by its absolute file path.
    /// </summary>
    /// <param name="requestContext">The MCP resource request context.</param>
    /// <param name="path">The absolute source document path.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The selected document resource contents.</returns>
    [McpServerResource(
        UriTemplate = DocumentUriTemplate,
        Name = "csls document",
        MimeType = "application/json")]
    [Description("One loaded C# document selected by its absolute source file path.")]
    public async Task<TextResourceContents> GetDocumentAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        [Description("Absolute loaded source document path.")]
        string path,
        CancellationToken cancellationToken)
    {
        string fullPath = ValidatePath(path);
        ControlDashboardSnapshot snapshot = await _controlClient
            .GetDashboardSnapshotAsync(
                new ControlDashboardRequest(),
                cancellationToken).ConfigureAwait(false);
        ControlDocumentInfo document = snapshot.Documents.FirstOrDefault(document =>
            document.FilePath is not null && PathsEqual(document.FilePath, fullPath))
            ?? throw new McpException($"No loaded csls document has path {fullPath}.");
        return CreateJsonResource(
            requestContext.Params.Uri,
            JsonSerializer.Serialize(
                document,
                ControlJsonSerializerContext.Default.ControlDocumentInfo));
    }

    /// <summary>
    /// Reads current compiler and analyzer diagnostics for one loaded source document.
    /// </summary>
    /// <param name="requestContext">The MCP resource request context.</param>
    /// <param name="path">The absolute source document path.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The current document diagnostic resource contents.</returns>
    [McpServerResource(
        UriTemplate = DiagnosticUriTemplate,
        Name = "csls document diagnostics",
        MimeType = "application/json")]
    [Description("Current compiler and analyzer diagnostics for one loaded C# document.")]
    public async Task<TextResourceContents> GetDiagnosticsAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        [Description("Absolute loaded source document path.")]
        string path,
        CancellationToken cancellationToken)
    {
        string fullPath = ValidatePath(path);
        DocumentDiagnosticReport diagnostics = await _controlClient
            .GetDiagnosticsAsync(
                new ControlDiagnosticRequest { DocumentPath = fullPath },
                cancellationToken).ConfigureAwait(false);
        return CreateJsonResource(
            requestContext.Params.Uri,
            JsonSerializer.Serialize(
                diagnostics,
                ControlJsonSerializerContext.Default.DocumentDiagnosticReport));
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

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathLength)
        {
            throw new McpException(
                $"path must contain between 1 and {MaximumPathLength} characters.");
        }

        return Path.GetFullPath(path);
    }
}
