using System.CommandLine;
using System.Globalization;
using static Csls.App.CliCommandOptions;

namespace Csls.App;

/// <summary>
/// Builds live request inspection and cancellation commands.
/// </summary>
internal static class RequestCommand
{
    /// <summary>
    /// Creates the complete command and its validated subcommands.
    /// </summary>
    /// <returns>The configured command.</returns>
    internal static Command Create()
    {
        var requestsCommand = new Command(
            "requests",
            "Inspect and cancel requests in a csls session.");
        Option<int?> requestsListSessionOption = CreateSessionOption();
        Option<string?> requestsListWorkspaceOption = CreateWorkspaceOption();
        Option<string?> requestsListCursorOption = CreateCursorOption();
        Option<int> requestsListLimitOption = CreateLimitOption();
        var requestsListJsonOption = new Option<bool>("--json")
        {
            Description = "Write the versioned machine-readable response envelope."
        };
        var requestsListCommand = new Command("list", "List queued and running requests.")
        {
            requestsListSessionOption,
            requestsListWorkspaceOption,
            requestsListCursorOption,
            requestsListLimitOption,
            requestsListJsonOption
        };
        AddSessionWorkspaceValidator(
            requestsListCommand,
            requestsListSessionOption,
            requestsListWorkspaceOption);
        requestsListCommand.SetAction((parseResult, cancellationToken) =>
            CliWorkerSupervisor.RunAsync(
                [
                    "requests-list",
                    (parseResult.GetValue(requestsListSessionOption) ?? 0)
                        .ToString(CultureInfo.InvariantCulture),
                    NormalizeWorkspacePath(parseResult.GetValue(requestsListWorkspaceOption)),
                    parseResult.GetValue(requestsListCursorOption) ?? string.Empty,
                    parseResult.GetValue(requestsListLimitOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(requestsListJsonOption).ToString(CultureInfo.InvariantCulture)
                ],
                cancellationToken));
        requestsCommand.Subcommands.Add(requestsListCommand);

        var correlationIdArgument = new Argument<Guid>("correlation-id")
        {
            Description = "Stable correlation identifier from the live request list."
        };
        correlationIdArgument.Validators.Add(static result =>
        {
            if (result.GetValueOrDefault<Guid>() == Guid.Empty)
            {
                result.AddError("correlation-id cannot be empty.");
            }
        });
        Option<int?> requestsCancelSessionOption = CreateSessionOption();
        Option<string?> requestsCancelWorkspaceOption = CreateWorkspaceOption();
        var requestsCancelJsonOption = new Option<bool>("--json")
        {
            Description = "Write the versioned machine-readable response envelope."
        };
        var requestsCancelCommand = new Command("cancel", "Cancel one queued or running request.")
        {
            correlationIdArgument,
            requestsCancelSessionOption,
            requestsCancelWorkspaceOption,
            requestsCancelJsonOption
        };
        AddSessionWorkspaceValidator(
            requestsCancelCommand,
            requestsCancelSessionOption,
            requestsCancelWorkspaceOption);
        requestsCancelCommand.SetAction((parseResult, cancellationToken) =>
            CliWorkerSupervisor.RunAsync(
                [
                    "requests-cancel",
                    (parseResult.GetValue(requestsCancelSessionOption) ?? 0)
                        .ToString(CultureInfo.InvariantCulture),
                    NormalizeWorkspacePath(parseResult.GetValue(requestsCancelWorkspaceOption)),
                    parseResult.GetRequiredValue(correlationIdArgument).ToString("D"),
                    parseResult.GetValue(requestsCancelJsonOption).ToString(CultureInfo.InvariantCulture)
                ],
                cancellationToken));
        requestsCommand.Subcommands.Add(requestsCancelCommand);
        return requestsCommand;
    }
}
