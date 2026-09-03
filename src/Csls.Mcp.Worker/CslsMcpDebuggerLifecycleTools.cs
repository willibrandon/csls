using Csls.Debugger.Contracts;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes explicit debugger lifecycle operations through supervised workers.
/// </summary>
[McpServerToolType]
internal sealed class CslsMcpDebuggerLifecycleTools
{
    private readonly McpDebuggerSessionBroker _broker;

    /// <summary>
    /// Creates lifecycle tools backed by the connection-owned debugger broker.
    /// </summary>
    /// <param name="broker">The shared debugger-session broker.</param>
    public CslsMcpDebuggerLifecycleTools(McpDebuggerSessionBroker broker)
    {
        ArgumentNullException.ThrowIfNull(broker);
        _broker = broker;
    }

    /// <summary>
    /// Launches a managed target in a new explicitly identified debugger session.
    /// </summary>
    /// <param name="program">The absolute managed executable or assembly path.</param>
    /// <param name="workingDirectory">The absolute target working directory.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="arguments">The ordered target arguments.</param>
    /// <param name="environment">Target environment additions and removals.</param>
    /// <param name="runtimeHostPath">The optional absolute managed runtime host path.</param>
    /// <param name="initialSourcePath">The optional initial source breakpoint path.</param>
    /// <param name="initialLine">The optional one-based initial breakpoint line.</param>
    /// <param name="agentControl">Whether this MCP connection may control execution.</param>
    /// <param name="justMyCode">Whether source stepping excludes non-user code.</param>
    /// <param name="enableStepFiltering">Whether stepping skips properties and operators.</param>
    /// <returns>The new explicit debugger-session identity and initial state.</returns>
    [McpServerTool(
        Name = "debug_session_start",
        Title = "Start .NET debugger session",
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        ReadOnly = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugSessionInfo))]
    [Description("Launch one managed .NET target in an isolated debugger worker and return its explicit debugSession identifier.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> StartAsync(
        [Description("Absolute managed executable or assembly path.")]
        string program,
        [Description("Absolute target working directory.")]
        string workingDirectory,
        CancellationToken cancellationToken,
        [Description("Ordered target arguments.")]
        IReadOnlyList<string>? arguments = null,
        [Description("Target environment additions; null values remove inherited variables.")]
        IReadOnlyDictionary<string, string?>? environment = null,
        [Description("Optional absolute dotnet or executable host path.")]
        string? runtimeHostPath = null,
        [Description("Optional absolute source path for an initial breakpoint.")]
        string? initialSourcePath = null,
        [Description("Optional one-based line paired with initialSourcePath.")]
        int? initialLine = null,
        [Description("Explicitly permit this MCP connection to resume, pause, step, or mutate the target.")]
        bool agentControl = false,
        [Description("Classify and step only user code by default.")]
        bool justMyCode = true,
        [Description("Skip managed properties and operators while stepping by default.")]
        bool enableStepFiltering = true)
    {
        return McpDebuggerToolResult.RunAsync(async () =>
        {
            McpDebuggerLaunchValidator.ValidateLaunch(
                program,
                workingDirectory,
                runtimeHostPath,
                initialSourcePath,
                initialLine);
            return await _broker.LaunchAsync(
                new DebugLaunchRequest
                {
                    Program = Path.GetFullPath(program),
                    WorkingDirectory = Path.GetFullPath(workingDirectory),
                    Arguments = arguments ?? [],
                    Environment = environment ??
                        new Dictionary<string, string?>(StringComparer.Ordinal),
                    RuntimeHostPath = runtimeHostPath is null
                        ? null
                        : Path.GetFullPath(runtimeHostPath),
                    JustMyCode = justMyCode,
                    EnableStepFiltering = enableStepFiltering
                },
                initialSourcePath is null ? null : Path.GetFullPath(initialSourcePath),
                initialLine,
                agentControl,
                cancellationToken).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Attaches a new explicitly identified debugger session to a managed process.
    /// </summary>
    /// <param name="processId">The positive operating-system process identifier.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="pause">Whether attachment immediately pauses the target.</param>
    /// <param name="agentControl">Whether this MCP connection may control execution.</param>
    /// <returns>The new explicit debugger-session identity and initial state.</returns>
    [McpServerTool(
        Name = "debug_session_attach",
        Title = "Attach .NET debugger session",
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        ReadOnly = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugSessionInfo))]
    [Description("Attach an isolated debugger worker to one explicit managed process and return its debugSession identifier.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> AttachAsync(
        [Description("Positive operating-system process identifier.")]
        int processId,
        CancellationToken cancellationToken,
        [Description("Pause the target immediately after attachment.")]
        bool pause = true,
        [Description("Explicitly permit this MCP connection to resume, pause, step, or mutate the target.")]
        bool agentControl = false)
    {
        return McpDebuggerToolResult.RunAsync(async () =>
        {
            McpDebuggerLaunchValidator.ValidateAttach(processId);
            return await _broker.AttachAsync(
                new DebugAttachRequest(processId),
                pause,
                agentControl,
                cancellationToken).ConfigureAwait(false);
        });
    }
}
