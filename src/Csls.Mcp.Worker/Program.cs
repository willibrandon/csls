using System.CommandLine;
using Csls.Control;
using Csls.Mcp.Worker;

var sessionOption = new Option<int?>("--session")
{
    Description = "Attach to the csls language-server process with this identifier."
};
var socketOption = new Option<string?>("--socket")
{
    Description = "Attach to this absolute csls Unix-domain-socket path."
};
var rootCommand = new RootCommand(
    "Expose a live csls language-server session through the Model Context Protocol.")
{
    sessionOption,
    socketOption
};
rootCommand.SetAction(async (parseResult, cancellationToken) =>
{
    int? processId = parseResult.GetValue(sessionOption);
    string? configuredSocketPath = parseResult.GetValue(socketOption);
    if (processId.HasValue == !string.IsNullOrWhiteSpace(configuredSocketPath))
    {
        await Console.Error.WriteLineAsync(
            "Specify exactly one of --session or --socket.").ConfigureAwait(false);
        return 2;
    }

    if (processId is <= 0)
    {
        await Console.Error.WriteLineAsync(
            "--session must be a positive process identifier.").ConfigureAwait(false);
        return 2;
    }

    string socketPath = processId.HasValue
        ? ControlEndpoint.GetSocketPath(processId.Value)
        : Path.GetFullPath(configuredSocketPath!);
    return await McpHost.RunAsync(socketPath, cancellationToken).ConfigureAwait(false);
});

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);
