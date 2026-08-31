using StreamJsonRpc;

namespace Csls.Workspaces;

/// <summary>
/// Hosts isolated design-time MSBuild requests over protocol-only standard streams.
/// </summary>
public static class MSBuildBuildHostServer
{
    /// <summary>
    /// Runs the build host until its client disconnects or cancellation is requested.
    /// </summary>
    /// <param name="input">The client-to-host protocol stream.</param>
    /// <param name="output">The host-to-client protocol stream.</param>
    /// <param name="cancellationToken">The host cancellation token.</param>
    /// <returns>A task that completes after all dispatched requests retire.</returns>
    public static async Task RunAsync(
        Stream input,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        using var formatter = new SystemTextJsonFormatter();
        using var messageHandler = new HeaderDelimitedMessageHandler(
            output,
            input,
            formatter);
        using var rpc = new JsonRpc(messageHandler)
        {
            CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
            DisplayName = "csls-msbuild-build-host"
        };
        rpc.AddLocalRpcTarget(new MSBuildBuildHostRpcTarget());
        rpc.StartListening();
        await rpc.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        await rpc.DispatchCompletion.ConfigureAwait(false);
    }
}
