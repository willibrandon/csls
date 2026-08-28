using Csls.App;
using System.CommandLine;
using System.Globalization;

var rootCommand = new RootCommand(
    "Fast C# language intelligence for editors, terminals, and agents.");
rootCommand.SetAction(
    static (_, cancellationToken) => WorkerSupervisor.RunAsync(cancellationToken));

var lspCommand = new Command("lsp", "Run the Language Server Protocol over standard I/O.");
lspCommand.SetAction(
    static (_, cancellationToken) => WorkerSupervisor.RunAsync(cancellationToken));
rootCommand.Subcommands.Add(lspCommand);

var debuggerOutputOption = new Option<string>("--output")
{
    Description = "Private directory used to store the verified debugger.",
    HelpName = "directory",
    Required = true
};
var debuggerArchiveOption = new Option<string?>("--archive")
{
    Description = "Use a previously downloaded official debugger archive.",
    HelpName = "path"
};
var debuggerInstallCommand = new Command(
    "install",
    "Install the verified Microsoft .NET debugger for the active platform.")
{
    debuggerOutputOption,
    debuggerArchiveOption
};
debuggerInstallCommand.SetAction((parseResult, cancellationToken) =>
    DebuggerInstaller.InstallAsync(
        parseResult.GetRequiredValue(debuggerOutputOption),
        parseResult.GetValue(debuggerArchiveOption),
        cancellationToken));
var debuggerCommand = new Command(
    "debugger",
    "Manage the Microsoft .NET debugger used by editor integrations.")
{
    debuggerInstallCommand
};
rootCommand.Subcommands.Add(debuggerCommand);

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
rootCommand.Subcommands.Add(sessionsCommand);

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
rootCommand.Subcommands.Add(dashboardCommand);

var doctorPathArgument = new Argument<string>("path")
{
    Description = "Workspace directory, solution, project, or C# document path.",
    DefaultValueFactory = static _ => Environment.CurrentDirectory
};
var doctorJsonOption = new Option<bool>("--json")
{
    Description = "Write the versioned machine-readable response envelope."
};
var doctorBinlogOption = new Option<string?>("--binlog")
{
    Description = "Build the workspace and write an MSBuild binary log to this path.",
    HelpName = "path"
};
var doctorCommand = new Command(
    "doctor",
    "Inspect SDK selection and load the workspace through a transient csls session.")
{
    doctorPathArgument,
    doctorJsonOption,
    doctorBinlogOption
};
doctorCommand.SetAction((parseResult, cancellationToken) =>
    CliWorkerSupervisor.RunAsync(
        [
            "doctor",
            Path.GetFullPath(parseResult.GetRequiredValue(doctorPathArgument)),
            NormalizeWorkspacePath(parseResult.GetValue(doctorBinlogOption)),
            parseResult.GetValue(doctorJsonOption).ToString(CultureInfo.InvariantCulture)
        ],
        cancellationToken));
rootCommand.Subcommands.Add(doctorCommand);

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
rootCommand.Subcommands.Add(workspaceCommand);

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
rootCommand.Subcommands.Add(requestsCommand);

var traceCommand = new Command("trace", "Control bounded request lifecycle tracing.");
traceCommand.Subcommands.Add(CreateTraceCommand(
    "start",
    "Start request lifecycle tracing for a session."));
traceCommand.Subcommands.Add(CreateTraceCommand(
    "stop",
    "Stop request lifecycle tracing and return its entries."));
rootCommand.Subcommands.Add(traceCommand);

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
rootCommand.Subcommands.Add(queryCommand);

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
            parseResult.GetValue(codeActionCursorOption) ?? string.Empty,
            parseResult.GetValue(codeActionLimitOption).ToString(CultureInfo.InvariantCulture),
            parseResult.GetValue(codeActionApplyOption).ToString(CultureInfo.InvariantCulture),
            parseResult.GetValue(codeActionJsonOption).ToString(CultureInfo.InvariantCulture)
        ],
        cancellationToken));
editCommand.Subcommands.Add(codeActionCommand);
rootCommand.Subcommands.Add(editCommand);
rootCommand.Subcommands.Add(AgentCommand.Create());

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static Option<int?> CreateSessionOption()
{
    var option = new Option<int?>("--session")
    {
        Description = "Language-server process identifier; inferred when exactly one session is live.",
        HelpName = "pid"
    };
    option.Validators.Add(static result =>
    {
        int? value = result.GetValueOrDefault<int?>();
        if (value is <= 0)
        {
            result.AddError("--session must be a positive process identifier.");
        }
    });
    return option;
}

static Option<int> CreatePositionOption(
    string name,
    string description,
    bool required = true)
{
    var option = new Option<int>(name)
    {
        Description = description,
        HelpName = "number",
        Required = required
    };
    option.Validators.Add(static result =>
    {
        if (result.GetValueOrDefault<int>() < 0)
        {
            result.AddError("Document positions cannot be negative.");
        }
    });
    return option;
}

static Option<string?> CreateWorkspaceOption() => new("--workspace")
{
    Description = "Select this workspace or start a transient session when none is live.",
    HelpName = "path"
};

static Option<string?> CreateCursorOption() => new("--cursor")
{
    Description = "Opaque continuation cursor returned by the previous JSON result page.",
    HelpName = "cursor"
};

static Option<int> CreateLimitOption()
{
    var option = new Option<int>("--limit")
    {
        Description = "Maximum number of result items from 1 through 200.",
        HelpName = "count",
        DefaultValueFactory = static _ => 100
    };
    option.Validators.Add(static result =>
    {
        if (result.GetValueOrDefault<int>() is < 1 or > 200)
        {
            result.AddError("--limit must be between 1 and 200.");
        }
    });
    return option;
}

static void AddSessionWorkspaceValidator(
    Command command,
    Option<int?> sessionOption,
    Option<string?> workspaceOption)
{
    command.Validators.Add(result =>
    {
        if (result.GetValue(sessionOption).HasValue &&
            !string.IsNullOrWhiteSpace(result.GetValue(workspaceOption)))
        {
            result.AddError("Specify --session or --workspace, but not both.");
        }
    });
}

static Command CreateWorkspaceOperationCommand(string name, string description)
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

static Command CreateTraceCommand(string name, string description)
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

static string NormalizeWorkspacePath(string? path) =>
    string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);

static Command CreateNavigationCommand(
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
