using Csls.Mcp;
using System.CommandLine;

var sessionOption = new Option<int?>("--session")
{
    Description = "Attach to the csls language-server process with this identifier."
};
var socketOption = new Option<string?>("--socket")
{
    Description = "Attach to this absolute csls Unix-domain-socket path."
};
var workspaceOption = new Option<string?>("--workspace")
{
    Description = "Start a transient csls session for this workspace path."
};
var rootCommand = new RootCommand(
    "Expose a live csls language-server session through the Model Context Protocol.")
{
    sessionOption,
    socketOption,
    workspaceOption
};
rootCommand.SetAction(async (parseResult, cancellationToken) =>
{
    int? processId = parseResult.GetValue(sessionOption);
    string? socketPath = parseResult.GetValue(socketOption);
    string? workspacePath = parseResult.GetValue(workspaceOption);
    int sourceCount = (processId.HasValue ? 1 : 0) +
        (string.IsNullOrWhiteSpace(socketPath) ? 0 : 1) +
        (string.IsNullOrWhiteSpace(workspacePath) ? 0 : 1);
    if (sourceCount != 1)
    {
        await Console.Error.WriteLineAsync(
            "Specify exactly one of --session, --socket, or --workspace.")
            .ConfigureAwait(false);
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
        workspacePath,
        cancellationToken).ConfigureAwait(false);
});

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);
