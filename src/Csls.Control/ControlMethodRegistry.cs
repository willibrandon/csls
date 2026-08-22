using Csls.Control.Contracts;
using Csls.Protocol;
using StreamJsonRpc;

namespace Csls.Control;

/// <summary>
/// Registers the versioned control method table explicitly without assembly scanning.
/// </summary>
internal static class ControlMethodRegistry
{
    /// <summary>
    /// Registers every implemented control request delegate.
    /// </summary>
    /// <param name="rpc">The StreamJsonRpc connection to configure.</param>
    /// <param name="target">The control target implementation.</param>
    internal static void Register(JsonRpc rpc, IControlRpcTarget target)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        ArgumentNullException.ThrowIfNull(target);
        rpc.AddLocalRpcMethod(
            ControlMethods.GetSession,
            new Func<CancellationToken, Task<ControlSessionInfo>>(target.GetSessionAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.GetHover,
            new Func<ControlHoverRequest, CancellationToken, Task<ControlHoverResult>>(
                target.GetHoverAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.GetDiagnostics,
            new Func<ControlDiagnosticRequest, CancellationToken, Task<DocumentDiagnosticReport>>(
                target.GetDiagnosticsAsync));
    }

    private static void AddParameterObjectMethod(
        JsonRpc rpc,
        string methodName,
        Delegate handler)
    {
        var attribute = new JsonRpcMethodAttribute(methodName)
        {
            UseSingleObjectParameterDeserialization = true
        };
        rpc.AddLocalRpcMethod(
            handler.Method,
            handler.Target ?? throw new InvalidOperationException(
                $"Control method {methodName} requires an instance target."),
            attribute);
    }
}
