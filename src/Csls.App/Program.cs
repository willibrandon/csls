using System.Globalization;
using System.CommandLine;
using Csls.App;

var rootCommand = new RootCommand(
    "Fast C# language intelligence for editors, terminals, and agents.");
rootCommand.SetAction(
    static (_, cancellationToken) => WorkerSupervisor.RunAsync(cancellationToken));

var lspCommand = new Command("lsp", "Run the Language Server Protocol over standard I/O.");
lspCommand.SetAction(
    static (_, cancellationToken) => WorkerSupervisor.RunAsync(cancellationToken));
rootCommand.Subcommands.Add(lspCommand);

var sessionsCommand = new Command("sessions", "Inspect live csls language-server sessions.");
var listJsonOption = new Option<bool>("--json")
{
    Description = "Write the versioned machine-readable response envelope."
};
var listCommand = new Command("list", "List every live csls session.")
{
    listJsonOption
};
listCommand.SetAction((parseResult, cancellationToken) =>
    CliWorkerSupervisor.RunAsync(
        ["sessions-list", parseResult.GetValue(listJsonOption).ToString(CultureInfo.InvariantCulture)],
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
rootCommand.Subcommands.Add(sessionsCommand);

var queryCommand = new Command("query", "Query language intelligence from a live csls session.");
var hoverDocumentArgument = new Argument<string>("document")
{
    Description = "Absolute or current-directory-relative C# document path."
};
Option<int> hoverLineOption = CreatePositionOption("--line", "Zero-based UTF-16 line number.");
Option<int> hoverCharacterOption = CreatePositionOption(
    "--character",
    "Zero-based UTF-16 character offset.");
Option<int?> hoverSessionOption = CreateSessionOption();
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
    hoverJsonOption
};
hoverCommand.SetAction((parseResult, cancellationToken) =>
    CliWorkerSupervisor.RunAsync(
        [
            "query-hover",
            (parseResult.GetValue(hoverSessionOption) ?? 0).ToString(CultureInfo.InvariantCulture),
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
    previousResultOption,
    diagnosticJsonOption
};
diagnosticCommand.SetAction((parseResult, cancellationToken) =>
    CliWorkerSupervisor.RunAsync(
        [
            "query-diagnostics",
            (parseResult.GetValue(diagnosticSessionOption) ?? 0)
                .ToString(CultureInfo.InvariantCulture),
            Path.GetFullPath(parseResult.GetRequiredValue(diagnosticDocumentArgument)),
            parseResult.GetValue(previousResultOption) ?? string.Empty,
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
    completionJsonOption
};
completionCommand.SetAction((parseResult, cancellationToken) =>
    CliWorkerSupervisor.RunAsync(
        [
            "query-completion",
            (parseResult.GetValue(completionSessionOption) ?? 0)
                .ToString(CultureInfo.InvariantCulture),
            Path.GetFullPath(parseResult.GetRequiredValue(completionDocumentArgument)),
            parseResult.GetValue(completionLineOption).ToString(CultureInfo.InvariantCulture),
            parseResult.GetValue(completionCharacterOption).ToString(CultureInfo.InvariantCulture),
            parseResult.GetValue(completionJsonOption).ToString(CultureInfo.InvariantCulture)
        ],
        cancellationToken));
queryCommand.Subcommands.Add(completionCommand);
queryCommand.Subcommands.Add(CreateNavigationCommand(
    "definition",
    "Find source definitions for the symbol at one document position.",
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
    documentSymbolsJsonOption
};
documentSymbolsCommand.SetAction((parseResult, cancellationToken) =>
    CliWorkerSupervisor.RunAsync(
        [
            "query-document-symbols",
            (parseResult.GetValue(documentSymbolsSessionOption) ?? 0)
                .ToString(CultureInfo.InvariantCulture),
            Path.GetFullPath(parseResult.GetRequiredValue(documentSymbolsArgument)),
            parseResult.GetValue(documentSymbolsJsonOption).ToString(CultureInfo.InvariantCulture)
        ],
        cancellationToken));
queryCommand.Subcommands.Add(documentSymbolsCommand);

var workspaceSymbolsArgument = new Argument<string>("pattern")
{
    Description = "Declaration name or fuzzy search pattern."
};
Option<int?> workspaceSymbolsSessionOption = CreateSessionOption();
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
    workspaceSymbolsJsonOption
};
workspaceSymbolsCommand.SetAction((parseResult, cancellationToken) =>
    CliWorkerSupervisor.RunAsync(
        [
            "query-workspace-symbols",
            (parseResult.GetValue(workspaceSymbolsSessionOption) ?? 0)
                .ToString(CultureInfo.InvariantCulture),
            parseResult.GetRequiredValue(workspaceSymbolsArgument),
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
    signatureJsonOption
};
signatureCommand.SetAction((parseResult, cancellationToken) =>
    CliWorkerSupervisor.RunAsync(
        [
            "query-signature-help",
            (parseResult.GetValue(signatureSessionOption) ?? 0)
                .ToString(CultureInfo.InvariantCulture),
            Path.GetFullPath(parseResult.GetRequiredValue(signatureDocumentArgument)),
            parseResult.GetValue(signatureLineOption).ToString(CultureInfo.InvariantCulture),
            parseResult.GetValue(signatureCharacterOption).ToString(CultureInfo.InvariantCulture),
            parseResult.GetValue(signatureJsonOption).ToString(CultureInfo.InvariantCulture)
        ],
        cancellationToken));
queryCommand.Subcommands.Add(signatureCommand);
rootCommand.Subcommands.Add(queryCommand);

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

static Option<int> CreatePositionOption(string name, string description)
{
    var option = new Option<int>(name)
    {
        Description = description,
        HelpName = "number",
        Required = true
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
        jsonOption
    };
    if (includeDeclarationOption)
    {
        command.Options.Add(includeDeclaration);
    }

    command.SetAction((parseResult, cancellationToken) =>
        CliWorkerSupervisor.RunAsync(
            [
                "query-navigation",
                name,
                (parseResult.GetValue(sessionOption) ?? 0)
                    .ToString(CultureInfo.InvariantCulture),
                Path.GetFullPath(parseResult.GetRequiredValue(documentArgument)),
                parseResult.GetValue(lineOption).ToString(CultureInfo.InvariantCulture),
                parseResult.GetValue(characterOption).ToString(CultureInfo.InvariantCulture),
                (includeDeclarationOption && parseResult.GetValue(includeDeclaration))
                    .ToString(CultureInfo.InvariantCulture),
                parseResult.GetValue(jsonOption).ToString(CultureInfo.InvariantCulture)
            ],
            cancellationToken));
    return command;
}
