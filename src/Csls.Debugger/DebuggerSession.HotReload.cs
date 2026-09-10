using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Coordinates generation-safe managed Hot Reload operations.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Applies one compiler-produced module update to the exact current debugger stop.
    /// </summary>
    /// <param name="request">The generation-bound compiler delta request.</param>
    /// <param name="cancellationToken">Cancels queueing and pre-commit validation.</param>
    /// <returns>The committed module and stopped-state generations.</returns>
    public async Task<DebugHotReloadResult> ApplyHotReloadAsync(
        DebugHotReloadRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.UpdatedTypes);
        ArgumentNullException.ThrowIfNull(request.RequiredCapabilities);
        ArgumentNullException.ThrowIfNull(request.UpdatedMethods);
        ArgumentNullException.ThrowIfNull(request.ActiveStatements);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.ModuleId);
        ArgumentOutOfRangeException.ThrowIfNegative(request.ExpectedModuleGeneration);
        byte[] metadataDelta = request.MetadataDelta.ToArray();
        byte[] ilDelta = request.IlDelta.ToArray();
        byte[] pdbDelta = request.PdbDelta.ToArray();
        DebugHotReloadResult? result = null;
        await _actor.InvokeAsync(
            async token =>
            {
                if (_state != DebugSessionState.Stopped ||
                    _debuggee is not CorDebugDebuggee managedDebuggee)
                {
                    throw new InvalidOperationException(
                        $"Hot Reload is unavailable while the debugger session is {_state}.");
                }

                if (_stopGeneration.Value != request.StopGeneration)
                {
                    throw new InvalidOperationException(
                        $"Hot Reload stop generation {request.StopGeneration} is stale; " +
                        $"the current stopped generation is {_stopGeneration.Value}.");
                }

                DebugStopGeneration nextStopGeneration = _stopGeneration.Next();
                (int moduleGeneration, IReadOnlyList<uint> updatedMethods,
                    IReadOnlyList<uint> updatedTypes) =
                    await managedDebuggee.ApplyHotReloadAsync(
                        request.ModuleId,
                        request.ExpectedModuleGeneration,
                        metadataDelta,
                        ilDelta,
                        pdbDelta,
                        request.UpdatedTypes,
                        request.RequiredCapabilities,
                        request.UpdatedMethods,
                        request.ActiveStatements,
                        token).ConfigureAwait(false);
                _stopGeneration = nextStopGeneration;
                result = new DebugHotReloadResult(
                    request.ModuleId,
                    moduleGeneration,
                    nextStopGeneration.Value,
                    updatedMethods,
                    updatedTypes);
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
