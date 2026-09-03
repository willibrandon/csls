using Csls.Control.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using ModelContextProtocol.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Mcp.Worker;

/// <summary>
/// Hosts the official MCP C# SDK over standard input and output for selected csls sessions.
/// </summary>
internal static class McpHost
{
    /// <summary>
    /// Runs the standard MCP transport until the client disconnects or cancellation is requested.
    /// </summary>
    /// <param name="cancellationToken">The host cancellation token.</param>
    /// <returns>The successful process exit code.</returns>
    internal static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var broker = new McpSessionBroker();
        await using ConfiguredAsyncDisposable brokerCleanup = broker.ConfigureAwait(false);
        string? debuggerWorkerPath = McpDebuggerWorkerLocator.TryResolve();
        var debuggerBroker = new McpDebuggerSessionBroker(debuggerWorkerPath);
        await using ConfiguredAsyncDisposable debuggerBrokerCleanup =
            debuggerBroker.ConfigureAwait(false);
        var tools = new CslsMcpTools(broker);
        var workspaceTools = new CslsMcpWorkspaceTools(broker);
        var requestTools = new CslsMcpRequestTools(broker);
        var resources = new CslsMcpResources(broker);
        var prompts = new CslsMcpPrompts();
        JsonSerializerOptions serializerOptions = ControlJson.CreateSerializerOptions();
        serializerOptions.TypeInfoResolverChain.Insert(
            0,
            McpJsonSerializerContext.Default);
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.FormatterName = ConsoleFormatterNames.Simple;
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        IMcpServerBuilder mcpBuilder = builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "csls",
                    Title = "csls C# language intelligence",
                    Version = typeof(McpHost).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                    Description = "Language intelligence and supervised .NET debugging for explicit sessions."
                };
                const string commonInstructions =
                    "Use read-only inspection tools before requesting edits. Document positions " +
                    "are zero-based UTF-16 line and character offsets.";
                options.ServerInstructions = debuggerBroker.IsAvailable
                    ? "Language tools require exactly one workspace, session, or socket selector. " +
                        "Debugger tools use only the explicit debugSession returned by a debugger " +
                        "lifecycle tool; never infer a debugger target from a workspace or process. " +
                        commonInstructions
                    : "Except for list_sessions, every tool and resource requires exactly one " +
                        "target selector: workspace, session, or socket. " +
                        commonInstructions;
            })
            .WithStdioServerTransport()
            .WithTools(tools, serializerOptions)
            .WithTools(workspaceTools, serializerOptions)
            .WithTools(requestTools, serializerOptions)
            .WithResources(resources)
            .WithPrompts(prompts);

        if (debuggerBroker.IsAvailable)
        {
            mcpBuilder
                .WithTools(
                    new CslsMcpDebuggerLifecycleTools(debuggerBroker),
                    serializerOptions)
                .WithTools(
                    new CslsMcpDebuggerSessionTools(debuggerBroker),
                    serializerOptions)
                .WithTools(
                    new CslsMcpDebuggerExecutionTools(debuggerBroker),
                    serializerOptions)
                .WithTools(
                    new CslsMcpDebuggerBreakpointTools(debuggerBroker),
                    serializerOptions)
                .WithTools(
                    new CslsMcpDebuggerInspectionTools(debuggerBroker),
                    serializerOptions)
                .WithResources(new CslsMcpDebuggerResources(debuggerBroker));
            mcpBuilder.WithPrompts(new CslsMcpDebuggerPrompts());
        }

        using IHost host = builder.Build();
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
