using Csls.Debugger.Contracts;

namespace Csls.Debugger.Dump;

/// <summary>
/// Rejects debugger operations that cannot mutate or execute a process dump.
/// </summary>
public sealed partial class DumpDebuggerControlService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<DebugSourceBreakpointInfo>> SetSourceBreakpointsAsync(
        DebugSourceBreakpointSetRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedAsync<IReadOnlyList<DebugSourceBreakpointInfo>>(
            request,
            "source breakpoints",
            cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<DebugFunctionBreakpointInfo>> SetFunctionBreakpointsAsync(
        DebugFunctionBreakpointSetRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedAsync<IReadOnlyList<DebugFunctionBreakpointInfo>>(
            request,
            "function breakpoints",
            cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<DebugInstructionBreakpointInfo>> SetInstructionBreakpointsAsync(
        DebugInstructionBreakpointSetRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedAsync<IReadOnlyList<DebugInstructionBreakpointInfo>>(
            request,
            "instruction breakpoints",
            cancellationToken);

    /// <inheritdoc />
    public Task SetExceptionBreakpointsAsync(
        DebugExceptionBreakpointSetRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedAsync<object>(request, "exception breakpoints", cancellationToken);

    /// <inheritdoc />
    public Task<DebugBreakpointSnapshot> GetBreakpointsAsync(
        CancellationToken cancellationToken) =>
        InvokeAsync(
            () =>
            {
                RequireOpen();
                return new DebugBreakpointSnapshot([], [], [], []);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<DebugExceptionInfo> GetExceptionInfoAsync(
        DebugExceptionInfoRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedAsync<DebugExceptionInfo>(
            request,
            "stop-cause exception inspection",
            cancellationToken);

    /// <inheritdoc />
    public Task<DebugSessionSnapshot> PauseAsync(CancellationToken cancellationToken) =>
        UnsupportedAsync<DebugSessionSnapshot>(null, "pause", cancellationToken);

    /// <inheritdoc />
    public Task<DebugSessionSnapshot> ContinueAsync(CancellationToken cancellationToken) =>
        UnsupportedAsync<DebugSessionSnapshot>(null, "continue", cancellationToken);

    /// <inheritdoc />
    public Task<DebugSessionSnapshot> StepAsync(
        DebugStepRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedAsync<DebugSessionSnapshot>(request, "stepping", cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<DebugStepTargetInfo>> GetStepTargetsAsync(
        DebugStepTargetsRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedAsync<IReadOnlyList<DebugStepTargetInfo>>(
            request,
            "step targets",
            cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<DebugGotoTargetInfo>> GetGotoTargetsAsync(
        DebugGotoTargetsRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedAsync<IReadOnlyList<DebugGotoTargetInfo>>(
            request,
            "go-to targets",
            cancellationToken);

    /// <inheritdoc />
    public Task<DebugSessionSnapshot> GotoAsync(
        DebugGotoRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedAsync<DebugSessionSnapshot>(request, "go to", cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<DebugScopeInfo>> GetScopesAsync(
        DebugScopesRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedAsync<IReadOnlyList<DebugScopeInfo>>(
            request,
            "managed local-variable recovery",
            cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<DebugVariableInfo>> GetVariablesAsync(
        DebugVariablesRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedAsync<IReadOnlyList<DebugVariableInfo>>(
            request,
            "managed variable expansion",
            cancellationToken);

    /// <inheritdoc />
    public Task<DebugEvaluateResult> EvaluateAsync(
        DebugEvaluateRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedAsync<DebugEvaluateResult>(request, "expression evaluation", cancellationToken);

    /// <inheritdoc />
    public Task<DebugEvaluateResult> ExecuteExpressionAsync(
        DebugExecuteExpressionRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedAsync<DebugEvaluateResult>(request, "expression execution", cancellationToken);

    /// <inheritdoc />
    public Task<DebugAssignmentResult> SetVariableAsync(
        DebugSetVariableRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedAsync<DebugAssignmentResult>(request, "variable assignment", cancellationToken);

    /// <inheritdoc />
    public Task<DebugAssignmentResult> SetExpressionAsync(
        DebugSetExpressionRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedAsync<DebugAssignmentResult>(request, "expression assignment", cancellationToken);

    /// <inheritdoc />
    public Task<DebugMemoryReadResult> ReadMemoryAsync(
        DebugMemoryReadRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedAsync<DebugMemoryReadResult>(request, "memory reads", cancellationToken);

    /// <inheritdoc />
    public Task<DebugDisassembly> DisassembleAsync(
        DebugDisassemblyRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedAsync<DebugDisassembly>(request, "managed IL disassembly", cancellationToken);

    /// <inheritdoc />
    public Task<DebugSourceContent> GetSourceContentAsync(
        DebugSourceRequest request,
        CancellationToken cancellationToken) =>
        UnsupportedAsync<DebugSourceContent>(request, "source retrieval", cancellationToken);

    /// <inheritdoc />
    public Task<DebugOutputPage> GetOutputAsync(
        DebugOutputRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync(
            () =>
            {
                RequireOpen();
                return new DebugOutputPage([], 0, 1, 0, false);
            },
            cancellationToken);
    }

    private static Task<T> UnsupportedAsync<T>(
        object? request,
        string operation,
        CancellationToken cancellationToken)
    {
        _ = request;
        cancellationToken.ThrowIfCancellationRequested();
        throw CreateReadOnlyException(operation);
    }
}
