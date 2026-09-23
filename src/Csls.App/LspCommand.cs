using System.CommandLine;

namespace Csls.App;

/// <summary>
/// Builds the standard-input language-server command.
/// </summary>
internal static class LspCommand
{
    /// <summary>
    /// Creates the language-server command.
    /// </summary>
    /// <returns>The configured command.</returns>
    internal static Command Create()
    {
        var command = new Command(
            "lsp",
            "Run the Language Server Protocol over standard I/O.");
        command.SetAction(
            static (_, cancellationToken) => WorkerSupervisor.RunAsync(cancellationToken));
        return command;
    }
}
