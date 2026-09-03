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

        var standardStreams = DebuggerWorkerStandardStreams.Open(stabilizeInput: true);
        await using ConfiguredAsyncDisposable cleanup = standardStreams.ConfigureAwait(false);
        var service = new DebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        await DebuggerRpcStreamServer.RunAsync(
            standardStreams.Input,
            standardStreams.Output,
            service,
            cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
