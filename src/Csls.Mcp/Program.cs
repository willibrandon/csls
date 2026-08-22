using System.CommandLine;
using Csls.Mcp;

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
    string? socketPath = parseResult.GetValue(socketOption);
    if (processId.HasValue == !string.IsNullOrWhiteSpace(socketPath))
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

    return await McpWorkerSupervisor.RunAsync(
        processId,
        socketPath,
        cancellationToken).ConfigureAwait(false);
});

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);
