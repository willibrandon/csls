using Csls.EndToEndPerformance;
using System.CommandLine;

const int DefaultTimeoutSeconds = 180;
const double DefaultStartupBudgetMilliseconds = 10_000;
const double DefaultWorkspaceLoadBudgetMilliseconds = 120_000;
const double DefaultReadyBudgetMilliseconds = 130_000;
const double DefaultMemoryBudgetMebibytes = 2_048;
const int DefaultProcessCountBudget = 32;
const long BytesPerMebibyte = 1024 * 1024;

var serverArgument = new Argument<string>("server")
{
    Description = "Absolute path to the published csls launcher."
};
var workspaceArgument = new Argument<string>("workspace")
{
    Description = "Absolute workspace directory measured by csls."
};
var outputOption = new Option<string>("--output")
{
    Description = "JSON report path.",
    DefaultValueFactory = static _ => Path.Join(
        Environment.CurrentDirectory,
        "artifacts",
        "end-to-end-performance",
        "results.json")
};
var iterationsOption = new Option<int>("--iterations")
{
    Description = "Number of fresh csls processes to measure.",
    DefaultValueFactory = static _ => 1
};
iterationsOption.Validators.Add(static result =>
{
    if (result.GetValueOrDefault<int>() <= 0)
    {
        result.AddError("--iterations must be greater than zero.");
    }
});
Option<int> timeoutOption = CreatePositiveOption(
    "--timeout-seconds",
    "Maximum duration for each iteration.",
    DefaultTimeoutSeconds);
Option<double> startupBudgetOption = CreatePositiveDoubleOption(
    "--startup-budget-ms",
    "Maximum median process-start through first protocol response time.",
    DefaultStartupBudgetMilliseconds);
Option<double> workspaceBudgetOption = CreatePositiveDoubleOption(
    "--workspace-budget-ms",
    "Maximum median initialize through workspace-ready time.",
    DefaultWorkspaceLoadBudgetMilliseconds);
Option<double> readyBudgetOption = CreatePositiveDoubleOption(
    "--ready-budget-ms",
    "Maximum median process-start through workspace-ready time.",
    DefaultReadyBudgetMilliseconds);
Option<double> workingSetBudgetOption = CreatePositiveDoubleOption(
    "--working-set-budget-mib",
    "Maximum process-tree working set.",
    DefaultMemoryBudgetMebibytes);
Option<double> privateMemoryBudgetOption = CreatePositiveDoubleOption(
    "--private-memory-budget-mib",
    "Maximum process-tree private memory.",
    DefaultMemoryBudgetMebibytes);
Option<int> processCountBudgetOption = CreatePositiveOption(
    "--process-count-budget",
    "Maximum ready-state process-tree count.",
    DefaultProcessCountBudget);

var rootCommand = new RootCommand(
    "Measure published csls startup, workspace load, memory, and process count.")
{
    serverArgument,
    workspaceArgument,
    outputOption,
    iterationsOption,
    timeoutOption,
    startupBudgetOption,
    workspaceBudgetOption,
    readyBudgetOption,
    workingSetBudgetOption,
    privateMemoryBudgetOption,
    processCountBudgetOption
};
rootCommand.SetAction((parseResult, cancellationToken) =>
{
    string serverPath = Path.GetFullPath(parseResult.GetRequiredValue(serverArgument));
    string workspacePath = Path.GetFullPath(parseResult.GetRequiredValue(workspaceArgument));
    string outputPath = Path.GetFullPath(parseResult.GetRequiredValue(outputOption));
    if (!File.Exists(serverPath))
    {
        throw new FileNotFoundException("The published csls launcher was not found.", serverPath);
    }

    if (!Directory.Exists(workspacePath))
    {
        throw new DirectoryNotFoundException(
            $"The measured workspace directory was not found: {workspacePath}");
    }

    return EndToEndPerformanceRunner.RunAsync(
        new PerformanceOptions
        {
            ServerPath = serverPath,
            WorkspacePath = workspacePath,
            OutputPath = outputPath,
            Iterations = parseResult.GetValue(iterationsOption),
            Timeout = TimeSpan.FromSeconds(parseResult.GetValue(timeoutOption)),
            StartupBudgetMilliseconds = parseResult.GetValue(startupBudgetOption),
            WorkspaceLoadBudgetMilliseconds = parseResult.GetValue(workspaceBudgetOption),
            ReadyBudgetMilliseconds = parseResult.GetValue(readyBudgetOption),
            WorkingSetBudgetBytes = checked(
                (long)(parseResult.GetValue(workingSetBudgetOption) * BytesPerMebibyte)),
            PrivateMemoryBudgetBytes = checked(
                (long)(parseResult.GetValue(privateMemoryBudgetOption) * BytesPerMebibyte)),
            ProcessCountBudget = parseResult.GetValue(processCountBudgetOption)
        },
        cancellationToken);
});

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static Option<int> CreatePositiveOption(string name, string description, int defaultValue)
{
    var option = new Option<int>(name)
    {
        Description = description,
        DefaultValueFactory = _ => defaultValue
    };
    option.Validators.Add(result =>
    {
        if (result.GetValueOrDefault<int>() <= 0)
        {
            result.AddError($"{name} must be greater than zero.");
        }
    });
    return option;
}

static Option<double> CreatePositiveDoubleOption(
    string name,
    string description,
    double defaultValue)
{
    var option = new Option<double>(name)
    {
        Description = description,
        DefaultValueFactory = _ => defaultValue
    };
    option.Validators.Add(result =>
    {
        double value = result.GetValueOrDefault<double>();
        if (!double.IsFinite(value) || value <= 0)
        {
            result.AddError($"{name} must be a finite number greater than zero.");
        }
    });
    return option;
}
