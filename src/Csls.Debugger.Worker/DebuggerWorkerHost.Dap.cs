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
        var standardStreams = DebuggerWorkerStandardStreams.Open();
        await using ConfiguredAsyncDisposable cleanup = standardStreams.ConfigureAwait(false);
        return await DebugAdapterHost.RunAsync(
            standardStreams.Input,
            standardStreams.Output,
            standardStreams.Error,
            cancellationToken).ConfigureAwait(false);
    }
}
