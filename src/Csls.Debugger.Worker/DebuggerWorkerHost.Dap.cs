using Csls.DebugAdapter;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Worker;

/// <summary>
/// Hosts Debug Adapter Protocol connections inside the supervised debugger worker.
/// </summary>
internal static partial class DebuggerWorkerHost
{
    private static async Task<int> RunDapAsync(CancellationToken cancellationToken)
    {
        Stream input = Console.OpenStandardInput();
        await using ConfiguredAsyncDisposable inputCleanup = input.ConfigureAwait(false);
        Stream output = Console.OpenStandardOutput();
        await using ConfiguredAsyncDisposable outputCleanup = output.ConfigureAwait(false);
        return await DebugAdapterHost.RunAsync(
            input,
            output,
            Console.Error,
            cancellationToken).ConfigureAwait(false);
    }
}
