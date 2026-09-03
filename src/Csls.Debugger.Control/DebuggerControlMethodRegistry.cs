using Csls.Debugger.Contracts;
using StreamJsonRpc;

namespace Csls.Debugger.Control;

/// <summary>
/// Registers the private debugger control method table without reflection scanning.
/// </summary>
internal static class DebuggerControlMethodRegistry
{
    /// <summary>
    /// Registers every debugger control operation.
    /// </summary>
    /// <param name="rpc">The server connection.</param>
    /// <param name="target">The debugger control target.</param>
    internal static void Register(JsonRpc rpc, IDebuggerControlTarget target)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        ArgumentNullException.ThrowIfNull(target);
        rpc.AddLocalRpcMethod(
            DebuggerControlMethods.GetProtocolVersion,
            new Func<int>(static () => DebuggerControlProtocol.CurrentVersion));
        rpc.AddLocalRpcMethod(
            DebuggerControlMethods.GetSession,
            new Func<CancellationToken, Task<DebugSessionSnapshot>>(target.GetSessionAsync));
        AddParameterObjectMethod(
            rpc,
            DebuggerControlMethods.Launch,
            new Func<DebugLaunchRequest, CancellationToken, Task<DebugSessionSnapshot>>(
                target.LaunchAsync));
        AddParameterObjectMethod(
            rpc,
            DebuggerControlMethods.Attach,
            new Func<DebugAttachRequest, CancellationToken, Task<DebugSessionSnapshot>>(
                target.AttachAsync));
        AddParameterObjectMethod(
            rpc,
            DebuggerControlMethods.SetSourceBreakpoints,
            new Func<DebugSourceBreakpointSetRequest, CancellationToken,
                Task<IReadOnlyList<DebugSourceBreakpointInfo>>>(target.SetSourceBreakpointsAsync));
        AddParameterObjectMethod(
            rpc,
            DebuggerControlMethods.SetFunctionBreakpoints,
            new Func<DebugFunctionBreakpointSetRequest, CancellationToken,
                Task<IReadOnlyList<DebugFunctionBreakpointInfo>>>(target.SetFunctionBreakpointsAsync));
        AddParameterObjectMethod(
            rpc,
            DebuggerControlMethods.SetExceptionBreakpoints,
            new Func<DebugExceptionBreakpointSetRequest, CancellationToken, Task>(
                target.SetExceptionBreakpointsAsync));
        AddParameterObjectMethod(
            rpc,
            DebuggerControlMethods.GetExceptionInfo,
            new Func<DebugExceptionInfoRequest, CancellationToken, Task<DebugExceptionInfo>>(
                target.GetExceptionInfoAsync));
        rpc.AddLocalRpcMethod(
            DebuggerControlMethods.Pause,
            new Func<CancellationToken, Task<DebugSessionSnapshot>>(target.PauseAsync));
        rpc.AddLocalRpcMethod(
            DebuggerControlMethods.Continue,
            new Func<CancellationToken, Task<DebugSessionSnapshot>>(target.ContinueAsync));
        AddParameterObjectMethod(
            rpc,
            DebuggerControlMethods.Step,
            new Func<DebugStepRequest, CancellationToken, Task<DebugSessionSnapshot>>(
                target.StepAsync));
        rpc.AddLocalRpcMethod(
            DebuggerControlMethods.GetThreads,
            new Func<CancellationToken, Task<IReadOnlyList<DebugThreadInfo>>>(
                target.GetThreadsAsync));
        AddParameterObjectMethod(
            rpc,
            DebuggerControlMethods.GetStack,
            new Func<DebugStackRequest, CancellationToken, Task<DebugStackTrace>>(
                target.GetStackAsync));
        AddParameterObjectMethod(
            rpc,
            DebuggerControlMethods.GetScopes,
            new Func<DebugScopesRequest, CancellationToken, Task<IReadOnlyList<DebugScopeInfo>>>(
                target.GetScopesAsync));
        AddParameterObjectMethod(
            rpc,
            DebuggerControlMethods.GetVariables,
            new Func<DebugVariablesRequest, CancellationToken,
                Task<IReadOnlyList<DebugVariableInfo>>>(target.GetVariablesAsync));
        AddParameterObjectMethod(
            rpc,
            DebuggerControlMethods.GetSourceContent,
            new Func<DebugSourceRequest, CancellationToken, Task<DebugSourceContent>>(
                target.GetSourceContentAsync));
        rpc.AddLocalRpcMethod(
            DebuggerControlMethods.Terminate,
            new Func<CancellationToken, Task<DebugSessionSnapshot>>(target.TerminateAsync));
        rpc.AddLocalRpcMethod(
            DebuggerControlMethods.Detach,
            new Func<CancellationToken, Task<DebugSessionSnapshot>>(target.DetachAsync));
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
                $"Debugger control method {methodName} requires an instance target."),
            attribute);
    }
}
