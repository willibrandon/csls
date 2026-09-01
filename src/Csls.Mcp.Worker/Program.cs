using Csls.Client;
using Csls.Control;
using Csls.Mcp.Worker;
using System.CommandLine;
using System.Runtime.CompilerServices;

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
    string? configuredSocketPath = parseResult.GetValue(socketOption);
    string? workspacePath = parseResult.GetValue(workspaceOption);
    int sourceCount = (processId.HasValue ? 1 : 0) +
        (string.IsNullOrWhiteSpace(configuredSocketPath) ? 0 : 1) +
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

    if (!string.IsNullOrWhiteSpace(workspacePath))
    {
        TransientLanguageServerSession transient =
            await TransientLanguageServerSession.StartInitializingAsync(
                workspacePath,
                "csls-mcp",
                cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable transientCleanup =
            transient.ConfigureAwait(false);
        using var hostSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<int> hostTask = McpHost.RunAsync(
            ControlEndpoint.GetSocketPath(transient.ProcessId),
            unlinkSocketAfterConnect: true,
            transient.WaitUntilReadyAsync,
            hostSource.Token);
        Task processTask = transient.WaitForExitAsync(CancellationToken.None);
        Task completed = await Task.WhenAny(hostTask, processTask).ConfigureAwait(false);
        if (completed == hostTask)
        {
            return await hostTask.ConfigureAwait(false);
        }

        int exitCode = transient.ExitCode == 0 ? 1 : transient.ExitCode;
        await hostSource.CancelAsync().ConfigureAwait(false);
        try
        {
            await hostTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (hostSource.IsCancellationRequested)
        {
            return exitCode;
        }

        return exitCode;
    }

    string socketPath = processId.HasValue
        ? ControlEndpoint.GetSocketPath(processId.Value)
        : Path.GetFullPath(configuredSocketPath!);
    return await McpHost.RunAsync(
        socketPath,
        unlinkSocketAfterConnect: false,
        workspaceReadiness: null,
        cancellationToken).ConfigureAwait(false);
});

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);
