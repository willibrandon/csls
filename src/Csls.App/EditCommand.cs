using System.CommandLine;
using System.Globalization;
using static Csls.App.CliCommandOptions;

namespace Csls.App;

/// <summary>
/// Builds preview-first semantic workspace edit commands.
/// </summary>
internal static class EditCommand
{
    /// <summary>
    /// Creates the complete command and its validated subcommands.
    /// </summary>
    /// <returns>The configured command.</returns>
    internal static Command Create()
    {
        var editCommand = new Command(
            "edit",
            "Preview semantic workspace edits through a csls session.");
        var renameDocumentArgument = new Argument<string>("document")
        {
            Description = "Absolute or current-directory-relative C# document path."
        };
        var renameNameArgument = new Argument<string>("new-name")
        {
            Description = "Valid replacement C# identifier."
        };
        Option<int> renameLineOption = CreatePositionOption("--line", "Zero-based UTF-16 line number.");
        Option<int> renameCharacterOption = CreatePositionOption(
            "--character",
            "Zero-based UTF-16 character offset.");
        Option<int?> renameSessionOption = CreateSessionOption();
        Option<string?> renameWorkspaceOption = CreateWorkspaceOption();
        var renameJsonOption = new Option<bool>("--json")
        {
            Description = "Write the versioned machine-readable response envelope."
        };
        var renameApplyOption = new Option<bool>("--apply")
        {
            Description = "Explicitly apply the one-use plan after all preconditions pass."
        };
        var renameCommand = new Command("rename", "Preview a semantic cross-document rename.")
        {
            renameDocumentArgument,
            renameNameArgument,
            renameLineOption,
            renameCharacterOption,
            renameSessionOption,
            renameWorkspaceOption,
            renameApplyOption,
            renameJsonOption
        };
        AddSessionWorkspaceValidator(renameCommand, renameSessionOption, renameWorkspaceOption);
        renameCommand.SetAction((parseResult, cancellationToken) =>
            CliWorkerSupervisor.RunAsync(
                [
                    "edit-rename",
                    (parseResult.GetValue(renameSessionOption) ?? 0)
                        .ToString(CultureInfo.InvariantCulture),
                    NormalizeWorkspacePath(parseResult.GetValue(renameWorkspaceOption)),
                    Path.GetFullPath(parseResult.GetRequiredValue(renameDocumentArgument)),
                    parseResult.GetValue(renameLineOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(renameCharacterOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetRequiredValue(renameNameArgument),
                    parseResult.GetValue(renameApplyOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(renameJsonOption).ToString(CultureInfo.InvariantCulture)
                ],
                cancellationToken));
        editCommand.Subcommands.Add(renameCommand);

        var formatDocumentArgument = new Argument<string>("document")
        {
            Description = "Absolute or current-directory-relative C# document path."
        };
        var formatTabSizeOption = new Option<int>("--tab-size")
        {
            Description = "Indentation width from 1 through 32.",
            HelpName = "number",
            DefaultValueFactory = static _ => 4
        };
        formatTabSizeOption.Validators.Add(static result =>
        {
            if (result.GetValueOrDefault<int>() is < 1 or > 32)
            {
                result.AddError("--tab-size must be between 1 and 32.");
            }
        });
        var formatTabsOption = new Option<bool>("--tabs")
        {
            Description = "Use tabs instead of spaces for indentation."
        };
        Option<int?> formatSessionOption = CreateSessionOption();
        Option<string?> formatWorkspaceOption = CreateWorkspaceOption();
        var formatJsonOption = new Option<bool>("--json")
        {
            Description = "Write the versioned machine-readable response envelope."
        };
        var formatApplyOption = new Option<bool>("--apply")
        {
            Description = "Explicitly apply the one-use plan after all preconditions pass."
        };
        var formatCommand = new Command("format", "Preview complete-document Roslyn formatting.")
        {
            formatDocumentArgument,
            formatTabSizeOption,
            formatTabsOption,
            formatSessionOption,
            formatWorkspaceOption,
            formatApplyOption,
            formatJsonOption
        };
        AddSessionWorkspaceValidator(formatCommand, formatSessionOption, formatWorkspaceOption);
        formatCommand.SetAction((parseResult, cancellationToken) =>
            CliWorkerSupervisor.RunAsync(
                [
                    "edit-format",
                    (parseResult.GetValue(formatSessionOption) ?? 0)
                        .ToString(CultureInfo.InvariantCulture),
                    NormalizeWorkspacePath(parseResult.GetValue(formatWorkspaceOption)),
                    Path.GetFullPath(parseResult.GetRequiredValue(formatDocumentArgument)),
                    parseResult.GetValue(formatTabSizeOption).ToString(CultureInfo.InvariantCulture),
                    (!parseResult.GetValue(formatTabsOption)).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(formatApplyOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(formatJsonOption).ToString(CultureInfo.InvariantCulture)
                ],
                cancellationToken));
        editCommand.Subcommands.Add(formatCommand);

        var codeActionDocumentArgument = new Argument<string>("document")
        {
            Description = "Absolute or current-directory-relative C# document path."
        };
        var codeActionKindOption = new Option<string>("--kind")
        {
            Description = "Hierarchical code-action category.",
            HelpName = "category",
            DefaultValueFactory = static _ => "source.organizeImports"
        };
        var codeActionTitleOption = new Option<string?>("--title")
        {
            Description = "Exact Roslyn code-action title to preview or apply.",
            HelpName = "title"
        };
        Option<int> codeActionLineOption = CreatePositionOption(
            "--line",
            "Zero-based line containing the code-action target.",
            required: false);
        Option<int> codeActionCharacterOption = CreatePositionOption(
            "--character",
            "Zero-based UTF-16 character containing the code-action target.",
            required: false);
        Option<int?> codeActionSessionOption = CreateSessionOption();
        Option<string?> codeActionWorkspaceOption = CreateWorkspaceOption();
        Option<string?> codeActionCursorOption = CreateCursorOption();
        Option<int> codeActionLimitOption = CreateLimitOption();
        var codeActionJsonOption = new Option<bool>("--json")
        {
            Description = "Write the versioned machine-readable response envelope."
        };
        var codeActionApplyOption = new Option<bool>("--apply")
        {
            Description = "Explicitly apply the single returned action after all preconditions pass."
        };
        var codeActionCommand = new Command("code-action", "Preview concrete Roslyn code actions.")
        {
            codeActionDocumentArgument,
            codeActionKindOption,
            codeActionTitleOption,
            codeActionLineOption,
            codeActionCharacterOption,
            codeActionSessionOption,
            codeActionWorkspaceOption,
            codeActionCursorOption,
            codeActionLimitOption,
            codeActionApplyOption,
            codeActionJsonOption
        };
        AddSessionWorkspaceValidator(
            codeActionCommand,
            codeActionSessionOption,
            codeActionWorkspaceOption);
        codeActionCommand.SetAction((parseResult, cancellationToken) =>
            CliWorkerSupervisor.RunAsync(
                [
                    "edit-code-action",
                    (parseResult.GetValue(codeActionSessionOption) ?? 0)
                        .ToString(CultureInfo.InvariantCulture),
                    NormalizeWorkspacePath(parseResult.GetValue(codeActionWorkspaceOption)),
                    Path.GetFullPath(parseResult.GetRequiredValue(codeActionDocumentArgument)),
                    parseResult.GetValue(codeActionLineOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(codeActionCharacterOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetRequiredValue(codeActionKindOption),
                    parseResult.GetValue(codeActionTitleOption) ?? string.Empty,
                    parseResult.GetValue(codeActionCursorOption) ?? string.Empty,
                    parseResult.GetValue(codeActionLimitOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(codeActionApplyOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(codeActionJsonOption).ToString(CultureInfo.InvariantCulture)
                ],
                cancellationToken));
        editCommand.Subcommands.Add(codeActionCommand);
        return editCommand;
    }
}
