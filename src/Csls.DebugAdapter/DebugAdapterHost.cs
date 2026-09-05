using System.Runtime.CompilerServices;

namespace Csls.DebugAdapter;

/// <summary>
/// Hosts one Debug Adapter Protocol session over explicit standard streams.
/// </summary>
public static class DebugAdapterHost
{
    /// <summary>
    /// Runs a DAP session until the client disconnects or the input stream closes.
    /// </summary>
    /// <param name="input">The protocol input stream.</param>
    /// <param name="output">The protocol output stream.</param>
    /// <param name="error">The diagnostics-only text stream.</param>
    /// <param name="cancellationToken">Cancels the debugger session.</param>
    /// <returns>Zero for a normal session or one for a terminal protocol failure.</returns>
    public static async Task<int> RunAsync(
        Stream input,
        Stream output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var session = new DapSession(input, output, error, cancellationToken);
        await using ConfiguredAsyncDisposable sessionDisposal = session.ConfigureAwait(false);
        return await session.RunAsync().ConfigureAwait(false);
    }
}
