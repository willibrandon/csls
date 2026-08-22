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

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);
