using System.CommandLine;
using System.Globalization;
using static Csls.App.CliCommandOptions;

namespace Csls.App;

/// <summary>
/// Builds the workspace and SDK diagnostics command.
/// </summary>
internal static class DoctorCommand
{
    /// <summary>
    /// Creates the complete command and its validated subcommands.
    /// </summary>
    /// <returns>The configured command.</returns>
    internal static Command Create()
    {
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
        return doctorCommand;
    }
}
