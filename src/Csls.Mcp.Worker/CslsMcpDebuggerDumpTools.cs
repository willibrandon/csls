using Csls.Debugger.Contracts;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Opens immutable managed process dumps through supervised debugger workers.
/// </summary>
[McpServerToolType]
internal sealed class CslsMcpDebuggerDumpTools
{
    private readonly McpDebuggerSessionBroker _broker;

    /// <summary>
    /// Creates process-dump tools backed by the connection-owned debugger broker.
    /// </summary>
    /// <param name="broker">The shared debugger-session broker.</param>
    public CslsMcpDebuggerDumpTools(McpDebuggerSessionBroker broker)
    {
        ArgumentNullException.ThrowIfNull(broker);
        _broker = broker;
    }

    /// <summary>
    /// Opens one managed process dump as an explicit read-only debugger session.
    /// </summary>
    /// <param name="dumpPath">The absolute existing process-dump path.</param>
    /// <param name="progress">Reports bounded dump activation progress to the requesting client.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="runtimeIndex">The zero-based runtime index for multi-runtime dumps.</param>
    /// <param name="dacPath">An optional absolute matching DAC path.</param>
    /// <returns>The new explicit read-only debugger-session identity.</returns>
    [McpServerTool(
        Name = "debug_dump_open",
        Title = "Open .NET process dump",
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        ReadOnly = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugSessionInfo))]
    [Description("Open one managed .NET process dump in an isolated read-only debugger worker and return its explicit debugSession identifier.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> OpenAsync(
        [Description("Absolute existing managed process-dump path.")]
        string dumpPath,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken,
        [Description("Zero-based managed runtime index for a dump containing multiple runtimes.")]
        int runtimeIndex = 0,
        [Description("Optional absolute matching DAC path for cross-runtime inspection.")]
        string? dacPath = null)
    {
        return McpDebuggerToolResult.RunAsync(async () =>
        {
            progress.Report(new ProgressNotificationValue
            {
                Progress = 0,
                Total = 2,
                Message = "Validating managed process dump."
            });
            Validate(dumpPath, runtimeIndex, dacPath);
            progress.Report(new ProgressNotificationValue
            {
                Progress = 1,
                Total = 2,
                Message = "Opening managed runtime and indexing dump state."
            });
            McpDebugSessionInfo session = await _broker.OpenDumpAsync(
                new DebugDumpOpenRequest(
                    Path.GetFullPath(dumpPath),
                    runtimeIndex,
                    dacPath is null ? null : Path.GetFullPath(dacPath)),
                cancellationToken).ConfigureAwait(false);
            progress.Report(new ProgressNotificationValue
            {
                Progress = 2,
                Total = 2,
                Message = "Managed process dump is ready."
            });
            return session;
        });
    }

    private static void Validate(string dumpPath, int runtimeIndex, string? dacPath)
    {
        ValidateExistingAbsoluteFile(dumpPath, "dumpPath");
        if (runtimeIndex < 0)
        {
            throw new McpDebuggerException(
                "debugger_request_invalid",
                "runtimeIndex must not be negative.");
        }

        if (dacPath is not null)
        {
            ValidateExistingAbsoluteFile(dacPath, "dacPath");
        }
    }

    private static void ValidateExistingAbsoluteFile(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new McpDebuggerException(
                "debugger_request_invalid",
                $"{name} must be an absolute path.");
        }

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new McpDebuggerException(
                "debugger_request_invalid",
                $"{name} does not exist: {fullPath}");
        }
    }
}
