using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Applies compiler-produced managed Hot Reload deltas to a stopped CoreCLR target.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Applies one validated module generation and refreshes source-level runtime state.
    /// </summary>
    /// <param name="moduleId">The stable session-local target module identifier.</param>
    /// <param name="expectedModuleGeneration">The module generation used to compile the deltas.</param>
    /// <param name="metadataDelta">The immutable ECMA-335 metadata delta.</param>
    /// <param name="ilDelta">The immutable managed IL delta.</param>
    /// <param name="pdbDelta">The immutable minimal Portable PDB delta.</param>
    /// <param name="activeStatements">The compiler-produced active-statement updates.</param>
    /// <param name="cancellationToken">Cancels validation before CoreCLR mutation begins.</param>
    /// <returns>The committed module generation and aggregate updated method tokens.</returns>
    internal async ValueTask<(int ModuleGeneration, IReadOnlyList<uint> UpdatedMethods)>
        ApplyHotReloadAsync(
            int moduleId,
            int expectedModuleGeneration,
            byte[] metadataDelta,
            byte[] ilDelta,
            byte[] pdbDelta,
            IReadOnlyList<DebugHotReloadActiveStatement> activeStatements,
            CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(moduleId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedModuleGeneration);
        CorDebugLoadedModule module = _sourceBreakpoints.FindModule(moduleId)
            ?? throw new KeyNotFoundException(
                $"Debugger module {moduleId} is no longer loaded.");
        if (module.HotReloadGeneration != expectedModuleGeneration)
        {
            throw new InvalidOperationException(
                $"Hot Reload module generation {expectedModuleGeneration} is stale; " +
                $"module {moduleId} is at generation {module.HotReloadGeneration}.");
        }

        if (module.IsHotReloadEnabled != true)
        {
            throw new NotSupportedException(
                module.HotReloadDiagnostic ??
                $"Module {module.Id} was not launched with Hot Reload enabled.");
        }

        HotReloadValidationResult update = HotReloadDeltaValidator.Validate(
            module,
            metadataDelta,
            ilDelta,
            pdbDelta,
            activeStatements);
        cancellationToken.ThrowIfCancellationRequested();
        ApplyRuntimeDeltas(module, metadataDelta, ilDelta);

        ClearFrameHandles();
        CancelStep();
        await _sourceBreakpoints.CommitHotReloadAsync(
            module,
            metadataDelta,
            pdbDelta,
            update.ActiveStatementRemaps,
            CancellationToken.None).ConfigureAwait(false);
        await _functionBreakpoints.RebindHotReloadModuleAsync(
            module.Identity,
            CancellationToken.None).ConfigureAwait(false);
        await _instructionBreakpoints.RebindHotReloadModuleAsync(
            module.Identity,
            CancellationToken.None).ConfigureAwait(false);
        return (module.HotReloadGeneration, update.UpdatedMethods);
    }

    private static unsafe void ApplyRuntimeDeltas(
        CorDebugLoadedModule module,
        byte[] metadataDelta,
        byte[] ilDelta)
    {
        if (!ComAbi.TryQueryInterface(
            module.Pointer,
            ICorDebugModule2Abi.InterfaceId,
            out nint module2))
        {
            throw new NotSupportedException(
                $"Module {module.Id} does not support managed Hot Reload.");
        }

        try
        {
            fixed (byte* metadata = metadataDelta)
            fixed (byte* il = ilDelta)
            {
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugModule2Abi(module2).ApplyChanges(
                        checked((uint)metadataDelta.Length),
                        (nint)metadata,
                        checked((uint)ilDelta.Length),
                        (nint)il),
                    "ICorDebugModule2.ApplyChanges");
            }
        }
        finally
        {
            _ = ComAbi.Release(module2);
        }
    }
}
