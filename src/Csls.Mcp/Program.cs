using Csls.Mcp;
using System.CommandLine;

var rootCommand = new RootCommand(
    "Expose explicitly selected csls workspaces and sessions through the Model Context Protocol.");
rootCommand.SetAction((_, cancellationToken) =>
    McpWorkerSupervisor.RunAsync(cancellationToken));

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);
