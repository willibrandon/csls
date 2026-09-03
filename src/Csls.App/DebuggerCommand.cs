using System.CommandLine;
using System.Globalization;

namespace Csls.App;

/// <summary>
/// Builds native .NET debugger adapter, terminal, and diagnostics commands.
/// </summary>
internal static class DebuggerCommand
{
    /// <summary>
    /// Creates the complete debugger command tree.
    /// </summary>
    /// <returns>The configured debugger command.</returns>
    internal static Command Create()
    {
        var command = new Command(
            "debugger",
            "Run native .NET debugging and editor debugger integration.");
        command.Subcommands.Add(CreateDapCommand());
        command.Subcommands.Add(CreateTerminalCommand());
        command.Subcommands.Add(CreateDoctorCommand());
        return command;
    }

    private static Command CreateDapCommand()
    {
        var command = new Command(
            "dap",
            "Run the csls Debug Adapter Protocol host over standard I/O.");
        command.SetAction(
            static (_, cancellationToken) => DebuggerWorkerSupervisor.RunAsync(
                ["dap"],
                cancellationToken));
        return command;
    }

    private static Command CreateTerminalCommand()
    {
        var command = new Command(
            "tui",
            "Debug managed applications in an interactive terminal.");
        command.Subcommands.Add(CreateLaunchCommand());
        command.Subcommands.Add(CreateAttachCommand());
        return command;
    }

    private static Command CreateLaunchCommand()
    {
        var programArgument = new Argument<string>("program")
        {
            Description = "Managed executable or assembly path."
        };
        var argumentsArgument = new Argument<string[]>("arguments")
        {
            Description = "Arguments passed to the managed target.",
            Arity = ArgumentArity.ZeroOrMore,
            DefaultValueFactory = static _ => []
        };
        var sourceOption = new Option<string>("--source")
        {
            Description = "Source document containing the initial breakpoint.",
            HelpName = "path",
            Required = true
        };
        var lineOption = new Option<int>("--line")
        {
            Description = "One-based line for the initial source breakpoint.",
            HelpName = "number",
            Required = true
        };
        lineOption.Validators.Add(static result =>
        {
            if (result.GetValueOrDefault<int>() <= 0)
            {
                result.AddError("--line must be a positive one-based source line.");
            }
        });
        var workingDirectoryOption = new Option<string>("--cwd")
        {
            Description = "Target working directory.",
            HelpName = "path",
            DefaultValueFactory = static _ => Environment.CurrentDirectory
        };
        var runtimeOption = new Option<string?>("--runtime")
        {
            Description = "Optional dotnet host path used to run a managed assembly.",
            HelpName = "path"
        };
        var command = new Command(
            "launch",
            "Launch a managed target and stop at an initial source breakpoint.")
        {
            programArgument,
            argumentsArgument,
            sourceOption,
            lineOption,
            workingDirectoryOption,
            runtimeOption
        };
        command.SetAction((parseResult, cancellationToken) =>
        {
            string? runtime = parseResult.GetValue(runtimeOption);
            return DebuggerWorkerSupervisor.RunAsync(
                [
                    "launch",
                    Path.GetFullPath(parseResult.GetRequiredValue(programArgument)),
                    Path.GetFullPath(parseResult.GetRequiredValue(workingDirectoryOption)),
                    Path.GetFullPath(parseResult.GetRequiredValue(sourceOption)),
                    parseResult.GetRequiredValue(lineOption)
                        .ToString(CultureInfo.InvariantCulture),
                    string.IsNullOrWhiteSpace(runtime) ? string.Empty : Path.GetFullPath(runtime),
                    .. parseResult.GetRequiredValue(argumentsArgument)
                ],
                cancellationToken);
        });
        return command;
    }

    private static Command CreateAttachCommand()
    {
        var processIdArgument = new Argument<int>("process-id")
        {
            Description = "Running managed process identifier."
        };
        processIdArgument.Validators.Add(static result =>
        {
            if (result.GetValueOrDefault<int>() <= 0)
            {
                result.AddError("process-id must be positive.");
            }
        });
        var command = new Command(
            "attach",
            "Attach to and pause a running CoreCLR process.")
        {
            processIdArgument
        };
        command.SetAction((parseResult, cancellationToken) =>
            DebuggerWorkerSupervisor.RunAsync(
                [
                    "attach",
                    parseResult.GetRequiredValue(processIdArgument)
                        .ToString(CultureInfo.InvariantCulture)
                ],
                cancellationToken));
        return command;
    }

    private static Command CreateDoctorCommand()
    {
        var command = new Command(
            "doctor",
            "Verify the packaged native .NET runtime-debugging components.");
        command.SetAction(
            static (_, cancellationToken) => DebuggerWorkerSupervisor.RunAsync(
                ["doctor"],
                cancellationToken));
        return command;
    }
}
