using Csls.Control;
using Csls.Control.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using ModelContextProtocol.Protocol;

namespace Csls.Mcp.Worker;

/// <summary>
/// Hosts the official MCP C# SDK over standard input and output for one csls session.
/// </summary>
internal static class McpHost
{
    /// <summary>
    /// Runs the standard MCP transport until the client disconnects or cancellation is requested.
    /// </summary>
    /// <param name="socketPath">The attached csls control-socket path.</param>
    /// <param name="unlinkSocketAfterConnect">Whether to unlink an exclusively owned socket after connecting.</param>
    /// <param name="cancellationToken">The host cancellation token.</param>
    /// <returns>The successful process exit code.</returns>
    internal static async Task<int> RunAsync(
        string socketPath,
        bool unlinkSocketAfterConnect,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        var controlClient = new ControlRpcClient(socketPath);
        try
        {
            if (unlinkSocketAfterConnect)
            {
                await controlClient.GetSessionAsync(cancellationToken).ConfigureAwait(false);
                File.Delete(socketPath);
            }

            var tools = new CslsMcpTools(controlClient);
            var workspaceTools = new CslsMcpWorkspaceTools(controlClient);
            var resources = new CslsMcpResources(controlClient);
            var prompts = new CslsMcpPrompts();
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
                        Description = "Language intelligence from an attached csls session."
                    };
                    options.ServerInstructions =
                        "Use read-only inspection tools before requesting edits. " +
                        "Document positions are zero-based UTF-16 line and character offsets.";
                })
                .WithStdioServerTransport()
                .WithTools(tools, ControlJson.CreateSerializerOptions())
                .WithTools(workspaceTools, ControlJson.CreateSerializerOptions())
                .WithResources(resources)
                .WithPrompts(prompts);

            using IHost host = builder.Build();
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            await controlClient.DisposeAsync().ConfigureAwait(false);
        }
    }
}
