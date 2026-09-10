using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Exposes bounded target output through the private debugger RPC client.
/// </summary>
public sealed partial class DebuggerRpcClient
{
    /// <summary>
    /// Gets one retained target-output page after a stable sequence cursor.
    /// </summary>
    /// <param name="request">The output cursor and maximum entry count.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The retained output page.</returns>
    public Task<DebugOutputPage> GetOutputAsync(
        DebugOutputRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugOutputRequest, DebugOutputPage>(
            DebuggerControlMethods.GetOutput,
            request,
            cancellationToken);
}
