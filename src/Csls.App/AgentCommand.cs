using System.CommandLine;
using System.Globalization;

namespace Csls.App;

/// <summary>
/// Creates commands that connect coding agents to csls through CLI and MCP interfaces.
/// </summary>
internal static class AgentCommand
{
    /// <summary>
    /// Creates the agent command group and its MCP and skill-file subcommands.
    /// </summary>
    /// <returns>The configured agent command.</returns>
    internal static Command Create()
    {
        var command = new Command(
            "agent",
            "Connect coding agents to csls through MCP and reusable instructions.");
        command.Subcommands.Add(CreateMcpCommand());
        command.Subcommands.Add(CreateInitCommand());
        return command;
    }

    private static Command CreateMcpCommand()
    {
        var sessionOption = new Option<int?>("--session")
        {
            Description = "Attach to the csls language-server process with this identifier."
        };
        sessionOption.Validators.Add(static result =>
        {
            if (result.GetValueOrDefault<int?>() is <= 0)
            {
                result.AddError("--session must be a positive process identifier.");
            }
        });
        var socketOption = new Option<string?>("--socket")
        {
            Description = "Attach to this absolute csls Unix-domain-socket path."
        };
        var workspaceOption = new Option<string?>("--workspace")
        {
            Description = "Start a transient csls session for this workspace path."
        };
        var command = new Command("mcp", "Launch the separately installed csls MCP server.")
        {
            sessionOption,
            socketOption,
            workspaceOption
        };
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            int? processId = parseResult.GetValue(sessionOption);
            string? socketPath = parseResult.GetValue(socketOption);
            string? workspacePath = parseResult.GetValue(workspaceOption);
            int sourceCount = (processId.HasValue ? 1 : 0) +
                (string.IsNullOrWhiteSpace(socketPath) ? 0 : 1) +
                (string.IsNullOrWhiteSpace(workspacePath) ? 0 : 1);
            if (sourceCount != 1)
            {
                await Console.Error.WriteLineAsync(
                    "Specify exactly one of --session, --socket, or --workspace.")
                    .ConfigureAwait(false);
                return 2;
            }

            return await AgentMcpSupervisor.RunAsync(
                processId,
                string.IsNullOrWhiteSpace(socketPath) ? null : Path.GetFullPath(socketPath),
                string.IsNullOrWhiteSpace(workspacePath) ? null : Path.GetFullPath(workspacePath),
                cancellationToken).ConfigureAwait(false);
        });
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
