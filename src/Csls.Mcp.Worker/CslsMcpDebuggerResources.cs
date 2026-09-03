using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes explicit debugger session and output state as MCP resource templates.
/// </summary>
[McpServerResourceType]
internal sealed class CslsMcpDebuggerResources
{
    private const string SessionTemplate = "csls://debug/session/{debugSession}";
    private const string OutputTemplate =
        "csls://debug/output/{debugSession}{?afterSequence,count}";
    private readonly McpDebuggerSessionBroker _broker;

    /// <summary>
    /// Creates resources backed by the connection-owned debugger broker.
    /// </summary>
    /// <param name="broker">The shared debugger-session broker.</param>
    public CslsMcpDebuggerResources(McpDebuggerSessionBroker broker)
    {
        ArgumentNullException.ThrowIfNull(broker);
        _broker = broker;
    }

    /// <summary>
    /// Reads current state for one explicit debugger session.
    /// </summary>
    [McpServerResource(
        UriTemplate = SessionTemplate,
        Name = "csls debugger session",
        MimeType = "application/json")]
    [Description("Current lifecycle and stop-generation state for one explicit debugger session.")]
    public Task<TextResourceContents> GetSessionAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        CancellationToken cancellationToken) =>
        ReadAsync(
            requestContext.Params.Uri,
            async () => JsonSerializer.Serialize(
                await _broker.GetAsync(debugSession, cancellationToken).ConfigureAwait(false),
                McpJsonSerializerContext.Default.McpDebugSessionInfo));

    /// <summary>
    /// Reads one bounded target-output page for an explicit debugger session.
    /// </summary>
    [McpServerResource(
        UriTemplate = OutputTemplate,
        Name = "csls debugger output",
        MimeType = "application/json")]
    [Description("Bounded stdout/stderr page after a stable debugger-session sequence cursor.")]
    public Task<TextResourceContents> GetOutputAsync(
        RequestContext<ReadResourceRequestParams> requestContext,
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        CancellationToken cancellationToken,
        [Description("Last observed sequence, or zero.")]
        string? afterSequence = null,
        [Description("Maximum entries from 1 through 256.")]
        string? count = null) =>
        ReadAsync(
            requestContext.Params.Uri,
            async () => JsonSerializer.Serialize(
                await _broker.GetOutputAsync(
                    debugSession,
                    Parse(afterSequence, 0, nameof(afterSequence)),
                    ParseCount(count),
                    cancellationToken).ConfigureAwait(false),
                McpJsonSerializerContext.Default.McpDebugOutputResult));

    private static async Task<TextResourceContents> ReadAsync(
        string uri,
        Func<Task<string>> readAsync)
    {
        try
        {
            return new TextResourceContents
            {
                Uri = uri,
                MimeType = "application/json",
                Text = await readAsync().ConfigureAwait(false)
            };
        }
        catch (McpDebuggerException exception)
        {
            throw new McpException($"{exception.Code}: {exception.Message}");
        }
    }

    private static long Parse(string? value, long defaultValue, string name)
    {
        if (value is null)
        {
            return defaultValue;
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long result))
        {
            throw InvalidResourceArgument(name);
        }

        return result;
    }

    private static McpDebuggerException InvalidResourceArgument(string name) =>
        new("debugger_request_invalid", $"{name} must be a non-negative integer.");

    private static int ParseCount(string? value)
    {
        long parsed = Parse(value, 128, "count");
        if (parsed > int.MaxValue)
        {
            throw InvalidResourceArgument("count");
        }

        return (int)parsed;
    }
}
