using System.CommandLine;
using System.Globalization;
using static Csls.App.CliCommandOptions;

namespace Csls.App;

/// <summary>
/// Builds explicit workspace maintenance commands.
/// </summary>
internal static class WorkspaceCommand
{
    /// <summary>
    /// Creates the complete command and its validated subcommands.
    /// </summary>
    /// <returns>The configured command.</returns>
    internal static Command Create()
    {
        var workspaceCommand = new Command(
            "workspace",
            "Maintain workspaces through a csls session.");
        workspaceCommand.Subcommands.Add(CreateWorkspaceOperationCommand(
            "restore",
            "Restore loaded solution and project entry points, then reload the workspace."));
        workspaceCommand.Subcommands.Add(CreateWorkspaceOperationCommand(
            "reload",
            "Reload the workspace while preserving unsaved document overlays."));
        workspaceCommand.Subcommands.Add(CreateWorkspaceOperationCommand(
            "restart-build-host",
            "Recreate Roslyn workspace hosts while preserving unsaved document overlays."));
        workspaceCommand.Subcommands.Add(CreateWorkspaceOperationCommand(
            "clear-cache",
            "Clear retained diagnostic, semantic-token, and pending-edit results."));
        return workspaceCommand;
    }

    private static Command CreateWorkspaceOperationCommand(string name, string description)
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
                    "workspace-operation",
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
