using System.CommandLine;

namespace Csls.App;

/// <summary>
/// Composes the top-level csls command from bounded feature command factories.
/// </summary>
internal static class RootCommandFactory
{
    /// <summary>
    /// Creates the complete csls command tree.
    /// </summary>
    /// <returns>The configured root command.</returns>
    internal static RootCommand Create()
    {
        var root = new RootCommand(
            "Fast C# language intelligence for editors, terminals, and agents.");
        root.SetAction(
            static (_, cancellationToken) => WorkerSupervisor.RunAsync(cancellationToken));
        root.Subcommands.Add(LspCommand.Create());
        root.Subcommands.Add(DebuggerCommand.Create());
        root.Subcommands.Add(SessionCommand.Create());
        root.Subcommands.Add(DashboardCommand.Create());
        root.Subcommands.Add(DoctorCommand.Create());
        root.Subcommands.Add(WorkspaceCommand.Create());
        root.Subcommands.Add(RequestCommand.Create());
        root.Subcommands.Add(TraceCommand.Create());
        root.Subcommands.Add(QueryCommand.Create());
        root.Subcommands.Add(EditCommand.Create());
        root.Subcommands.Add(AgentCommand.Create());
        return root;
    }
}
