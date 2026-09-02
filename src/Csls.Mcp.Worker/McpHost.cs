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
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "csls",
                    Title = "csls C# language intelligence",
                    Version = typeof(McpHost).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                    Description = "Language intelligence for explicitly selected csls workspaces and sessions."
                };
                options.ServerInstructions =
                    "Except for list_sessions, every tool and resource requires exactly one " +
                    "target selector: workspace, session, or socket. Use read-only inspection " +
                    "tools before requesting edits. Document positions are zero-based UTF-16 " +
                    "line and character offsets.";
            })
            .WithStdioServerTransport()
            .WithTools(tools, serializerOptions)
            .WithTools(workspaceTools, serializerOptions)
            .WithTools(requestTools, serializerOptions)
            .WithResources(resources)
            .WithPrompts(prompts);

        using IHost host = builder.Build();
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
