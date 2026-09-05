using System.CommandLine;
using System.Globalization;
using static Csls.App.CliCommandOptions;

namespace Csls.App;

/// <summary>
/// Builds bounded request tracing commands.
/// </summary>
internal static class TraceCommand
{
    /// <summary>
    /// Creates the complete command and its validated subcommands.
    /// </summary>
    /// <returns>The configured command.</returns>
    internal static Command Create()
    {
        var traceCommand = new Command("trace", "Control bounded request lifecycle tracing.");
        traceCommand.Subcommands.Add(CreateTraceCommand(
            "start",
            "Start request lifecycle tracing for a session."));
        traceCommand.Subcommands.Add(CreateTraceCommand(
            "stop",
            "Stop request lifecycle tracing and return its entries."));
        return traceCommand;
    }

    private static Command CreateTraceCommand(string name, string description)
    {
        Option<int?> sessionOption = CreateSessionOption();
        Option<string?> workspaceOption = CreateWorkspaceOption();
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Write the versioned machine-readable response envelope."
        };
        var command = new Command(name, description)
        {
            sessionOption,
            workspaceOption,
            jsonOption
        };
        AddSessionWorkspaceValidator(command, sessionOption, workspaceOption);
        command.SetAction((parseResult, cancellationToken) =>
            CliWorkerSupervisor.RunAsync(
                [
                    "trace-operation",
                    name,
                    (parseResult.GetValue(sessionOption) ?? 0)
                        .ToString(CultureInfo.InvariantCulture),
                    NormalizeWorkspacePath(parseResult.GetValue(workspaceOption)),
                    parseResult.GetValue(jsonOption).ToString(CultureInfo.InvariantCulture)
                ],
                cancellationToken));
        return command;
    }
}
