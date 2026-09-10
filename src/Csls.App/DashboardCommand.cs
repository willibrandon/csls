using System.CommandLine;
using System.Globalization;
using static Csls.App.CliCommandOptions;

namespace Csls.App;

/// <summary>
/// Builds the interactive language-server dashboard command.
/// </summary>
internal static class DashboardCommand
{
    /// <summary>
    /// Creates the complete command and its validated subcommands.
    /// </summary>
    /// <returns>The configured command.</returns>
    internal static Command Create()
    {
        Option<int?> dashboardSessionOption = CreateSessionOption();
        Option<string?> dashboardWorkspaceOption = CreateWorkspaceOption();
        var dashboardCommand = new Command(
            "dashboard",
            "Inspect language-server state in the Hex1b dashboard.")
        {
            dashboardSessionOption,
            dashboardWorkspaceOption
        };
        AddSessionWorkspaceValidator(
            dashboardCommand,
            dashboardSessionOption,
            dashboardWorkspaceOption);
        dashboardCommand.SetAction((parseResult, cancellationToken) =>
            CliWorkerSupervisor.RunAsync(
                [
                    "dashboard",
                    (parseResult.GetValue(dashboardSessionOption) ?? 0)
                        .ToString(CultureInfo.InvariantCulture),
                    NormalizeWorkspacePath(parseResult.GetValue(dashboardWorkspaceOption))
                ],
                cancellationToken));
        return dashboardCommand;
    }
}
