using Csls.Debugger.Control;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Worker;

/// <summary>
/// Hosts private debugger RPC for a supervising parent process.
/// </summary>
internal static partial class DebuggerWorkerHost
{
    private static async Task<int> RunControlAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 1)
        {
            throw new InvalidDataException(
                "The debugger control worker accepts no positional arguments.");
        }

        Stream input = Console.OpenStandardInput();
        await using ConfiguredAsyncDisposable inputCleanup = input.ConfigureAwait(false);
        Stream output = Console.OpenStandardOutput();
        await using ConfiguredAsyncDisposable outputCleanup = output.ConfigureAwait(false);
        var service = new DebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        await DebuggerRpcStreamServer.RunAsync(
            input,
            output,
            service,
            cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
