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
        var sourceOption = new Option<string?>("--source")
        {
            Description = "Source document containing the initial breakpoint.",
            HelpName = "path"
        };
        var lineOption = new Option<int?>("--line")
        {
            Description = "One-based line for the initial source breakpoint.",
            HelpName = "number"
        };
        lineOption.Validators.Add(static result =>
        {
            if (result.GetValueOrDefault<int?>() is <= 0)
            {
                result.AddError("--line must be a positive one-based source line.");
            }
        });
        var stopAtEntryOption = new Option<bool>("--stop-at-entry")
        {
            Description = "Stop at the first executable entry-point statement."
        };
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
        var environmentFileOption = new Option<string?>("--env-file")
        {
            Description = "UTF-8 environment file, absolute or relative to --cwd.",
            HelpName = "path"
        };
        environmentFileOption.Validators.Add(static result =>
        {
            if (result.GetValueOrDefault<string?>() is string path && string.IsNullOrWhiteSpace(path))
            {
                result.AddError("--env-file must name an environment file.");
            }
        });
        Option<string[]> sourceFileMapOption = CreateSourceFileMapOption();
        var command = new Command(
            "launch",
            "Launch a managed target and stop at entry or an initial source breakpoint.")
        {
            programArgument,
            argumentsArgument,
            sourceOption,
            lineOption,
            stopAtEntryOption,
            workingDirectoryOption,
            environmentFileOption,
            runtimeOption,
            sourceFileMapOption
        };
        command.Validators.Add(result =>
        {
            bool hasSource = result.GetValue(sourceOption) is not null;
            bool hasLine = result.GetValue(lineOption).HasValue;
            if (hasSource != hasLine)
            {
                result.AddError("Specify --source and --line together.");
            }
            else if (!hasSource && !result.GetValue(stopAtEntryOption))
            {
                result.AddError("Choose --stop-at-entry or an initial breakpoint with --source and --line.");
            }
        });
        command.SetAction((parseResult, cancellationToken) =>
        {
            string? runtime = parseResult.GetValue(runtimeOption);
            string? source = parseResult.GetValue(sourceOption);
            Dictionary<string, string> sourceFileMap = ParseSourceFileMap(
                parseResult.GetValue(sourceFileMapOption));
            return DebuggerWorkerSupervisor.RunAsync(
                [
                    "launch",
                    Path.GetFullPath(parseResult.GetRequiredValue(programArgument)),
                    Path.GetFullPath(parseResult.GetRequiredValue(workingDirectoryOption)),
                    source is null ? string.Empty : Path.GetFullPath(source),
                    (parseResult.GetValue(lineOption) ?? 0)
                        .ToString(CultureInfo.InvariantCulture),
                    string.IsNullOrWhiteSpace(runtime) ? string.Empty : Path.GetFullPath(runtime),
                    parseResult.GetValue(stopAtEntryOption) ? "true" : "false",
                    parseResult.GetValue(environmentFileOption) ?? string.Empty,
                    sourceFileMap.Count.ToString(CultureInfo.InvariantCulture),
                    .. sourceFileMap.SelectMany(static mapping =>
                        new[] { mapping.Key, mapping.Value }),
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
        Option<string[]> sourceFileMapOption = CreateSourceFileMapOption();
        var command = new Command(
            "attach",
            "Attach to and pause a running CoreCLR process.")
        {
            processIdArgument,
            sourceFileMapOption
        };
        command.SetAction((parseResult, cancellationToken) =>
        {
            Dictionary<string, string> sourceFileMap = ParseSourceFileMap(
                parseResult.GetValue(sourceFileMapOption));
            return DebuggerWorkerSupervisor.RunAsync(
                [
                    "attach",
                    parseResult.GetRequiredValue(processIdArgument)
                        .ToString(CultureInfo.InvariantCulture),
                    sourceFileMap.Count.ToString(CultureInfo.InvariantCulture),
                    .. sourceFileMap.SelectMany(static mapping =>
                        new[] { mapping.Key, mapping.Value })
                ],
                cancellationToken);
        });
        return command;
    }

    private static Option<string[]> CreateSourceFileMapOption()
    {
        var option = new Option<string[]>("--source-file-map")
        {
            Description =
                "Map an absolute PDB build-path prefix to an absolute local source prefix.",
            HelpName = "build=local",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true
        };
        option.Validators.Add(static result =>
        {
            var buildPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (string entry in result.GetValueOrDefault<string[]>() ?? [])
            {
                int separator = entry.IndexOf('=', StringComparison.Ordinal);
                if (separator <= 0 || separator == entry.Length - 1)
                {
                    result.AddError(
                        "--source-file-map values must use the form <absolute-build-path>=<absolute-local-path>.");
                    continue;
                }

                string buildPath = entry[..separator];
                string localPath = entry[(separator + 1)..];
                if (!IsPortableAbsolutePath(buildPath) || !IsPortableAbsolutePath(localPath))
                {
                    result.AddError(
                        "--source-file-map build and local paths must both be absolute.");
                }
                else if (!buildPaths.Add(buildPath))
                {
                    result.AddError(
                        $"--source-file-map contains the build path more than once: {buildPath}");
                }
            }
        });
        return option;
    }

    private static Dictionary<string, string> ParseSourceFileMap(
        IReadOnlyList<string>? entries)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string entry in entries ?? [])
        {
            int separator = entry.IndexOf('=', StringComparison.Ordinal);
            result.Add(entry[..separator], entry[(separator + 1)..]);
        }

        return result;
    }

    private static bool IsPortableAbsolutePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (path[0] == '/' ||
            path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.Length >= 3 &&
            char.IsAsciiLetter(path[0]) &&
            path[1] == ':' &&
            path[2] is '/' or '\\');

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
