using System.CommandLine;
using System.Globalization;
using static Csls.App.CliCommandOptions;

namespace Csls.App;

/// <summary>
/// Builds live language-server session inspection commands.
/// </summary>
internal static class SessionCommand
{
    /// <summary>
    /// Creates the complete command and its validated subcommands.
    /// </summary>
    /// <returns>The configured command.</returns>
    internal static Command Create()
    {
        var sessionsCommand = new Command("sessions", "Inspect live csls language-server sessions.");
        var listJsonOption = new Option<bool>("--json")
        {
            Description = "Write the versioned machine-readable response envelope."
        };
        Option<string?> listCursorOption = CreateCursorOption();
        Option<int> listLimitOption = CreateLimitOption();
        var listCommand = new Command("list", "List every live csls session.")
        {
            listCursorOption,
            listLimitOption,
            listJsonOption
        };
        listCommand.SetAction((parseResult, cancellationToken) =>
            CliWorkerSupervisor.RunAsync(
                [
                    "sessions-list",
                    parseResult.GetValue(listCursorOption) ?? string.Empty,
                    parseResult.GetValue(listLimitOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(listJsonOption).ToString(CultureInfo.InvariantCulture)
                ],
                cancellationToken));
        sessionsCommand.Subcommands.Add(listCommand);

        Option<int?> showSessionOption = CreateSessionOption();
        var showJsonOption = new Option<bool>("--json")
        {
            Description = "Write the versioned machine-readable response envelope."
        };
        var showCommand = new Command("show", "Show one live csls session.")
        {
            showSessionOption,
            showJsonOption
        };
        showCommand.SetAction((parseResult, cancellationToken) =>
            CliWorkerSupervisor.RunAsync(
                [
                    "sessions-show",
                    (parseResult.GetValue(showSessionOption) ?? 0).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(showJsonOption).ToString(CultureInfo.InvariantCulture)
                ],
                cancellationToken));
        sessionsCommand.Subcommands.Add(showCommand);

        var watchJsonOption = new Option<bool>("--json")
        {
            Description = "Write one versioned JSON envelope per observed session change."
        };
        var watchCommand = new Command("watch", "Watch live csls sessions until canceled.")
        {
            watchJsonOption
        };
        watchCommand.SetAction((parseResult, cancellationToken) =>
            CliWorkerSupervisor.RunAsync(
                [
                    "sessions-watch",
                    parseResult.GetValue(watchJsonOption).ToString(CultureInfo.InvariantCulture)
                ],
                cancellationToken));
        sessionsCommand.Subcommands.Add(watchCommand);
        return sessionsCommand;
    }
}
