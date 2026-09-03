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
        rpc.AddLocalRpcMethod(
            DebuggerControlMethods.Restart,
            new Func<CancellationToken, Task<DebugSessionSnapshot>>(target.RestartAsync));
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
            DebuggerControlMethods.SetInstructionBreakpoints,
            new Func<DebugInstructionBreakpointSetRequest, CancellationToken,
                Task<IReadOnlyList<DebugInstructionBreakpointInfo>>>(
                    target.SetInstructionBreakpointsAsync));
        AddParameterObjectMethod(
            rpc,
            DebuggerControlMethods.SetExceptionBreakpoints,
            new Func<DebugExceptionBreakpointSetRequest, CancellationToken, Task>(
                target.SetExceptionBreakpointsAsync));
        rpc.AddLocalRpcMethod(
            DebuggerControlMethods.GetBreakpoints,
            new Func<CancellationToken, Task<DebugBreakpointSnapshot>>(
                target.GetBreakpointsAsync));
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
        AddParameterObjectMethod(
            rpc,
            DebuggerControlMethods.GetStepTargets,
            new Func<DebugStepTargetsRequest, CancellationToken,
                Task<IReadOnlyList<DebugStepTargetInfo>>>(target.GetStepTargetsAsync));
        AddParameterObjectMethod(
            rpc,
            DebuggerControlMethods.GetGotoTargets,
            new Func<DebugGotoTargetsRequest, CancellationToken,
                Task<IReadOnlyList<DebugGotoTargetInfo>>>(target.GetGotoTargetsAsync));
        AddParameterObjectMethod(
            rpc,
            DebuggerControlMethods.Goto,
            new Func<DebugGotoRequest, CancellationToken, Task<DebugSessionSnapshot>>(
                target.GotoAsync));
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
            DebuggerControlMethods.Evaluate,
            new Func<DebugEvaluateRequest, CancellationToken, Task<DebugEvaluateResult>>(
                target.EvaluateAsync));
        AddParameterObjectMethod(
            rpc,
            DebuggerControlMethods.GetModules,
            new Func<DebugModulesRequest, CancellationToken, Task<DebugModulePage>>(
                target.GetModulesAsync));
        AddParameterObjectMethod(
            rpc,
            DebuggerControlMethods.GetOutput,
            new Func<DebugOutputRequest, CancellationToken, Task<DebugOutputPage>>(
                target.GetOutputAsync));
        AddParameterObjectMethod(
            rpc,
            DebuggerControlMethods.ReadMemory,
            new Func<DebugMemoryReadRequest, CancellationToken, Task<DebugMemoryReadResult>>(
                target.ReadMemoryAsync));
        AddParameterObjectMethod(
            rpc,
            DebuggerControlMethods.Disassemble,
            new Func<DebugDisassemblyRequest, CancellationToken, Task<DebugDisassembly>>(
                target.DisassembleAsync));
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
