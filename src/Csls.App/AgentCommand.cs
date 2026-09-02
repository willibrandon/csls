using System.CommandLine;
using System.Globalization;

namespace Csls.App;

/// <summary>
/// Creates commands that prepare coding agents to use csls CLI and MCP interfaces.
/// </summary>
internal static class AgentCommand
{
    /// <summary>
    /// Creates the agent command group and its skill-file subcommand.
    /// </summary>
    /// <returns>The configured agent command.</returns>
    internal static Command Create()
    {
        var command = new Command(
            "agent",
            "Create reusable csls instructions for coding agents.");
        command.Subcommands.Add(CreateInitCommand());
        return command;
    }

    private static Command CreateInitCommand()
    {
        var pathOption = new Option<string?>("--path")
        {
            Description = "Write the skill file to this path instead of ./SKILL.md.",
            HelpName = "path"
        };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Replace an existing skill file."
        };
        var stdoutOption = new Option<bool>("--stdout")
        {
            Description = "Write the skill content to standard output instead of a file."
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Write the versioned machine-readable response envelope."
        };
        var command = new Command("init", "Create a reusable csls agent skill file.")
        {
            pathOption,
            forceOption,
            stdoutOption,
            jsonOption
        };
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool writeStdout = parseResult.GetValue(stdoutOption);
            bool writeJson = parseResult.GetValue(jsonOption);
            string? configuredPath = parseResult.GetValue(pathOption);
            bool force = parseResult.GetValue(forceOption);
            if (writeStdout &&
                (writeJson || force || !string.IsNullOrWhiteSpace(configuredPath)))
            {
                await Console.Error.WriteLineAsync(
                    "--stdout cannot be combined with --path, --force, or --json.")
                    .ConfigureAwait(false);
                return 2;
            }

            string outputPath = writeStdout
                ? string.Empty
                : Path.GetFullPath(configuredPath ?? "SKILL.md");
            return await CliWorkerSupervisor.RunAsync(
                [
                    "agent-init",
                    outputPath,
                    force.ToString(CultureInfo.InvariantCulture),
                    writeStdout.ToString(CultureInfo.InvariantCulture),
                    writeJson.ToString(CultureInfo.InvariantCulture)
                ],
                cancellationToken).ConfigureAwait(false);
        });
        return command;
    }
}
