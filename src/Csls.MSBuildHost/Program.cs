using Csls.Workspaces;

using Stream input = Console.OpenStandardInput();
using Stream output = Console.OpenStandardOutput();
await MSBuildBuildHostServer.RunAsync(input, output).ConfigureAwait(false);
