using System.CommandLine;
using System.Globalization;
using static Csls.App.CliCommandOptions;

namespace Csls.App;

/// <summary>
/// Builds read-only language-intelligence query commands.
/// </summary>
internal static class QueryCommand
{
    /// <summary>
    /// Creates the complete command and its validated subcommands.
    /// </summary>
    /// <returns>The configured command.</returns>
    internal static Command Create()
    {
        var queryCommand = new Command("query", "Query language intelligence from a csls session.");
        var hoverDocumentArgument = new Argument<string>("document")
        {
            Description = "Absolute or current-directory-relative C# document path."
        };
        Option<int> hoverLineOption = CreatePositionOption("--line", "Zero-based UTF-16 line number.");
        Option<int> hoverCharacterOption = CreatePositionOption(
            "--character",
            "Zero-based UTF-16 character offset.");
        Option<int?> hoverSessionOption = CreateSessionOption();
        Option<string?> hoverWorkspaceOption = CreateWorkspaceOption();
        var hoverJsonOption = new Option<bool>("--json")
        {
            Description = "Write the versioned machine-readable response envelope."
        };
        var hoverCommand = new Command("hover", "Resolve hover information at a document position.")
        {
            hoverDocumentArgument,
            hoverLineOption,
            hoverCharacterOption,
            hoverSessionOption,
            hoverWorkspaceOption,
            hoverJsonOption
        };
        AddSessionWorkspaceValidator(hoverCommand, hoverSessionOption, hoverWorkspaceOption);
        hoverCommand.SetAction((parseResult, cancellationToken) =>
            CliWorkerSupervisor.RunAsync(
                [
                    "query-hover",
                    (parseResult.GetValue(hoverSessionOption) ?? 0).ToString(CultureInfo.InvariantCulture),
                    NormalizeWorkspacePath(parseResult.GetValue(hoverWorkspaceOption)),
                    Path.GetFullPath(parseResult.GetRequiredValue(hoverDocumentArgument)),
                    parseResult.GetValue(hoverLineOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(hoverCharacterOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(hoverJsonOption).ToString(CultureInfo.InvariantCulture)
                ],
                cancellationToken));
        queryCommand.Subcommands.Add(hoverCommand);

        var diagnosticDocumentArgument = new Argument<string>("document")
        {
            Description = "Absolute or current-directory-relative C# document path."
        };
        Option<int?> diagnosticSessionOption = CreateSessionOption();
        Option<string?> diagnosticWorkspaceOption = CreateWorkspaceOption();
        Option<string?> diagnosticCursorOption = CreateCursorOption();
        Option<int> diagnosticLimitOption = CreateLimitOption();
        var previousResultOption = new Option<string?>("--previous-result-id")
        {
            Description = "Opaque result identifier from a prior diagnostic response.",
            HelpName = "id"
        };
        var diagnosticJsonOption = new Option<bool>("--json")
        {
            Description = "Write the versioned machine-readable response envelope."
        };
        var diagnosticCommand = new Command(
            "diagnostics",
            "Get compiler and analyzer diagnostics for one document.")
        {
            diagnosticDocumentArgument,
            diagnosticSessionOption,
            diagnosticWorkspaceOption,
            previousResultOption,
            diagnosticCursorOption,
            diagnosticLimitOption,
            diagnosticJsonOption
        };
        AddSessionWorkspaceValidator(
            diagnosticCommand,
            diagnosticSessionOption,
            diagnosticWorkspaceOption);
        diagnosticCommand.SetAction((parseResult, cancellationToken) =>
            CliWorkerSupervisor.RunAsync(
                [
                    "query-diagnostics",
                    (parseResult.GetValue(diagnosticSessionOption) ?? 0)
                        .ToString(CultureInfo.InvariantCulture),
                    NormalizeWorkspacePath(parseResult.GetValue(diagnosticWorkspaceOption)),
                    Path.GetFullPath(parseResult.GetRequiredValue(diagnosticDocumentArgument)),
                    parseResult.GetValue(previousResultOption) ?? string.Empty,
                    parseResult.GetValue(diagnosticCursorOption) ?? string.Empty,
                    parseResult.GetValue(diagnosticLimitOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(diagnosticJsonOption).ToString(CultureInfo.InvariantCulture)
                ],
                cancellationToken));
        queryCommand.Subcommands.Add(diagnosticCommand);

        var completionDocumentArgument = new Argument<string>("document")
        {
            Description = "Absolute or current-directory-relative C# document path."
        };
        Option<int> completionLineOption = CreatePositionOption(
            "--line",
            "Zero-based UTF-16 line number.");
        Option<int> completionCharacterOption = CreatePositionOption(
            "--character",
            "Zero-based UTF-16 character offset.");
        Option<int?> completionSessionOption = CreateSessionOption();
        Option<string?> completionWorkspaceOption = CreateWorkspaceOption();
        Option<string?> completionCursorOption = CreateCursorOption();
        Option<int> completionLimitOption = CreateLimitOption();
        var completionJsonOption = new Option<bool>("--json")
        {
            Description = "Write the versioned machine-readable response envelope."
        };
        var completionCommand = new Command(
            "completion",
            "Get bounded Roslyn completion candidates at one document position.")
        {
            completionDocumentArgument,
            completionLineOption,
            completionCharacterOption,
            completionSessionOption,
            completionWorkspaceOption,
            completionCursorOption,
            completionLimitOption,
            completionJsonOption
        };
        AddSessionWorkspaceValidator(
            completionCommand,
            completionSessionOption,
            completionWorkspaceOption);
        completionCommand.SetAction((parseResult, cancellationToken) =>
            CliWorkerSupervisor.RunAsync(
                [
                    "query-completion",
                    (parseResult.GetValue(completionSessionOption) ?? 0)
                        .ToString(CultureInfo.InvariantCulture),
                    NormalizeWorkspacePath(parseResult.GetValue(completionWorkspaceOption)),
                    Path.GetFullPath(parseResult.GetRequiredValue(completionDocumentArgument)),
                    parseResult.GetValue(completionLineOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(completionCharacterOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(completionCursorOption) ?? string.Empty,
                    parseResult.GetValue(completionLimitOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(completionJsonOption).ToString(CultureInfo.InvariantCulture)
                ],
                cancellationToken));
        queryCommand.Subcommands.Add(completionCommand);
        queryCommand.Subcommands.Add(CreateNavigationCommand(
            "definition",
            "Find source definitions for the symbol at one document position.",
            includeDeclarationOption: false));
        queryCommand.Subcommands.Add(CreateNavigationCommand(
            "declaration",
            "Find source declarations for the symbol at one document position.",
            includeDeclarationOption: false));
        queryCommand.Subcommands.Add(CreateNavigationCommand(
            "type-definition",
            "Find source definitions for the symbol's type.",
            includeDeclarationOption: false));
        queryCommand.Subcommands.Add(CreateNavigationCommand(
            "implementation",
            "Find source implementations for the symbol at one document position.",
            includeDeclarationOption: false));
        queryCommand.Subcommands.Add(CreateNavigationCommand(
            "selection-range",
            "Get the nested syntax selection at one document position.",
            includeDeclarationOption: false));
        queryCommand.Subcommands.Add(CreateNavigationCommand(
            "highlights",
            "Get semantic symbol occurrences within one document.",
            includeDeclarationOption: false));
        queryCommand.Subcommands.Add(CreateNavigationCommand(
            "references",
            "Find source references for the symbol at one document position.",
            includeDeclarationOption: true));

        var documentSymbolsArgument = new Argument<string>("document")
        {
            Description = "Absolute or current-directory-relative C# document path."
        };
        Option<int?> documentSymbolsSessionOption = CreateSessionOption();
        Option<string?> documentSymbolsWorkspaceOption = CreateWorkspaceOption();
        Option<string?> documentSymbolsCursorOption = CreateCursorOption();
        Option<int> documentSymbolsLimitOption = CreateLimitOption();
        var documentSymbolsJsonOption = new Option<bool>("--json")
        {
            Description = "Write the versioned machine-readable response envelope."
        };
        var documentSymbolsCommand = new Command(
            "document-symbols",
            "Get the hierarchical declarations in one document.")
        {
            documentSymbolsArgument,
            documentSymbolsSessionOption,
            documentSymbolsWorkspaceOption,
            documentSymbolsCursorOption,
            documentSymbolsLimitOption,
            documentSymbolsJsonOption
        };
        AddSessionWorkspaceValidator(
            documentSymbolsCommand,
            documentSymbolsSessionOption,
            documentSymbolsWorkspaceOption);
        documentSymbolsCommand.SetAction((parseResult, cancellationToken) =>
            CliWorkerSupervisor.RunAsync(
                [
                    "query-document-symbols",
                    (parseResult.GetValue(documentSymbolsSessionOption) ?? 0)
                        .ToString(CultureInfo.InvariantCulture),
                    NormalizeWorkspacePath(parseResult.GetValue(documentSymbolsWorkspaceOption)),
                    Path.GetFullPath(parseResult.GetRequiredValue(documentSymbolsArgument)),
                    parseResult.GetValue(documentSymbolsCursorOption) ?? string.Empty,
                    parseResult.GetValue(documentSymbolsLimitOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(documentSymbolsJsonOption).ToString(CultureInfo.InvariantCulture)
                ],
                cancellationToken));
        queryCommand.Subcommands.Add(documentSymbolsCommand);

        var workspaceSymbolsArgument = new Argument<string>("pattern")
        {
            Description = "Declaration name or fuzzy search pattern."
        };
        Option<int?> workspaceSymbolsSessionOption = CreateSessionOption();
        Option<string?> workspaceSymbolsWorkspaceOption = CreateWorkspaceOption();
        Option<string?> workspaceSymbolsCursorOption = CreateCursorOption();
        Option<int> workspaceSymbolsLimitOption = CreateLimitOption();
        var workspaceSymbolsJsonOption = new Option<bool>("--json")
        {
            Description = "Write the versioned machine-readable response envelope."
        };
        var workspaceSymbolsCommand = new Command(
            "symbols",
            "Search source declarations across the current workspace.")
        {
            workspaceSymbolsArgument,
            workspaceSymbolsSessionOption,
            workspaceSymbolsWorkspaceOption,
            workspaceSymbolsCursorOption,
            workspaceSymbolsLimitOption,
            workspaceSymbolsJsonOption
        };
        AddSessionWorkspaceValidator(
            workspaceSymbolsCommand,
            workspaceSymbolsSessionOption,
            workspaceSymbolsWorkspaceOption);
        workspaceSymbolsCommand.SetAction((parseResult, cancellationToken) =>
            CliWorkerSupervisor.RunAsync(
                [
                    "query-workspace-symbols",
                    (parseResult.GetValue(workspaceSymbolsSessionOption) ?? 0)
                        .ToString(CultureInfo.InvariantCulture),
                    NormalizeWorkspacePath(parseResult.GetValue(workspaceSymbolsWorkspaceOption)),
                    parseResult.GetRequiredValue(workspaceSymbolsArgument),
                    parseResult.GetValue(workspaceSymbolsCursorOption) ?? string.Empty,
                    parseResult.GetValue(workspaceSymbolsLimitOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(workspaceSymbolsJsonOption).ToString(CultureInfo.InvariantCulture)
                ],
                cancellationToken));
        queryCommand.Subcommands.Add(workspaceSymbolsCommand);

        var signatureDocumentArgument = new Argument<string>("document")
        {
            Description = "Absolute or current-directory-relative C# document path."
        };
        Option<int> signatureLineOption = CreatePositionOption(
            "--line",
            "Zero-based UTF-16 line number.");
        Option<int> signatureCharacterOption = CreatePositionOption(
            "--character",
            "Zero-based UTF-16 character offset.");
        Option<int?> signatureSessionOption = CreateSessionOption();
        Option<string?> signatureWorkspaceOption = CreateWorkspaceOption();
        var signatureJsonOption = new Option<bool>("--json")
        {
            Description = "Write the versioned machine-readable response envelope."
        };
        var signatureCommand = new Command(
            "signature-help",
            "Get overload-aware signature help at one document position.")
        {
            signatureDocumentArgument,
            signatureLineOption,
            signatureCharacterOption,
            signatureSessionOption,
            signatureWorkspaceOption,
            signatureJsonOption
        };
        AddSessionWorkspaceValidator(
            signatureCommand,
            signatureSessionOption,
            signatureWorkspaceOption);
        signatureCommand.SetAction((parseResult, cancellationToken) =>
            CliWorkerSupervisor.RunAsync(
                [
                    "query-signature-help",
                    (parseResult.GetValue(signatureSessionOption) ?? 0)
                        .ToString(CultureInfo.InvariantCulture),
                    NormalizeWorkspacePath(parseResult.GetValue(signatureWorkspaceOption)),
                    Path.GetFullPath(parseResult.GetRequiredValue(signatureDocumentArgument)),
                    parseResult.GetValue(signatureLineOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(signatureCharacterOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(signatureJsonOption).ToString(CultureInfo.InvariantCulture)
                ],
                cancellationToken));
        queryCommand.Subcommands.Add(signatureCommand);
        return queryCommand;
    }

    private static Command CreateNavigationCommand(
        string name,
        string description,
        bool includeDeclarationOption)
    {
        var documentArgument = new Argument<string>("document")
        {
            Description = "Absolute or current-directory-relative C# document path."
        };
        Option<int> lineOption = CreatePositionOption(
            "--line",
            "Zero-based UTF-16 line number.");
        Option<int> characterOption = CreatePositionOption(
            "--character",
            "Zero-based UTF-16 character offset.");
        Option<int?> sessionOption = CreateSessionOption();
        Option<string?> workspaceOption = CreateWorkspaceOption();
        Option<string?> cursorOption = CreateCursorOption();
        Option<int> limitOption = CreateLimitOption();
        var includeDeclaration = new Option<bool>("--include-declaration")
        {
            Description = "Include symbol declaration locations in reference results."
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Write the versioned machine-readable response envelope."
        };
        var command = new Command(name, description)
        {
            documentArgument,
            lineOption,
            characterOption,
            sessionOption,
            workspaceOption,
            cursorOption,
            limitOption,
            jsonOption
        };
        if (includeDeclarationOption)
        {
            command.Options.Add(includeDeclaration);
        }

        AddSessionWorkspaceValidator(command, sessionOption, workspaceOption);

        command.SetAction((parseResult, cancellationToken) =>
            CliWorkerSupervisor.RunAsync(
                [
                    "query-navigation",
                    name,
                    (parseResult.GetValue(sessionOption) ?? 0)
                        .ToString(CultureInfo.InvariantCulture),
                    NormalizeWorkspacePath(parseResult.GetValue(workspaceOption)),
                    Path.GetFullPath(parseResult.GetRequiredValue(documentArgument)),
                    parseResult.GetValue(lineOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(characterOption).ToString(CultureInfo.InvariantCulture),
                    (includeDeclarationOption && parseResult.GetValue(includeDeclaration))
                        .ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(cursorOption) ?? string.Empty,
                    parseResult.GetValue(limitOption).ToString(CultureInfo.InvariantCulture),
                    parseResult.GetValue(jsonOption).ToString(CultureInfo.InvariantCulture)
                ],
                cancellationToken));
        return command;
    }
}
