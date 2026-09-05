using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Commits validated Hot Reload generations and refreshes source breakpoint bindings.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
    /// <summary>
    /// Records one applied module generation and rebinds its source breakpoints.
    /// </summary>
    /// <param name="module">The retained module successfully changed by CoreCLR.</param>
    /// <param name="metadataDelta">The validated immutable metadata delta.</param>
    /// <param name="pdbDelta">The validated immutable Portable PDB delta.</param>
    /// <param name="activeStatementRemaps">The validated old-to-current instruction maps.</param>
    /// <param name="cancellationToken">Cancels breakpoint-change notifications.</param>
    /// <returns>A task completed after all affected source breakpoints are rebound.</returns>
    internal async ValueTask CommitHotReloadAsync(
        CorDebugLoadedModule module,
        byte[] metadataDelta,
        byte[] pdbDelta,
        IReadOnlyList<HotReloadActiveStatementRemap> activeStatementRemaps,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(metadataDelta);
        ArgumentNullException.ThrowIfNull(pdbDelta);
        ArgumentNullException.ThrowIfNull(activeStatementRemaps);
        if (!_modules.TryGetValue(module.Identity, out CorDebugLoadedModule? retained) ||
            !ReferenceEquals(module, retained))
        {
            throw new InvalidOperationException(
                $"Module {module.Id} was unloaded before its Hot Reload update committed.");
        }

        module.MetadataDeltas.Add([.. metadataDelta]);
        module.SymbolDeltas.Add([.. pdbDelta]);
        module.HotReloadGeneration = checked(module.HotReloadGeneration + 1);
        foreach (HotReloadActiveStatementRemap remap in activeStatementRemaps)
        {
            _hotReloadRemaps.Add(
                (module.Identity, remap.MethodToken, remap.MethodVersion, remap.OldIlOffset),
                remap.NewIlOffset);
        }

        RemoveModuleBindings(module.Identity);
        ClearSources();
        foreach (List<SourceBreakpointDefinition> definitions in _definitions.Values)
        {
            await BindModuleAsync(
                module,
                definitions,
                notifyChanges: true,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves a runtime remap opportunity to its compiler-validated current IL offset.
    /// </summary>
    /// <param name="oldFunction">The borrowed old-version ICorDebugFunction pointer.</param>
    /// <param name="oldIlOffset">The runtime-reported old managed IL offset.</param>
    /// <param name="newIlOffset">Receives the current managed IL offset.</param>
    /// <returns>True when the compiler supplied an exact active-statement mapping.</returns>
    internal unsafe bool TryGetHotReloadRemap(
        nint oldFunction,
        uint oldIlOffset,
        out uint newIlOffset)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfZero(oldFunction);
        newIlOffset = 0;
        nint function2 = 0;
        nint module = 0;
        try
        {
            uint methodToken = 0;
            uint* methodTokenAddress = &methodToken;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugFunctionAbi(oldFunction).GetToken((nint)methodTokenAddress),
                "ICorDebugFunction.GetToken");
            methodToken = Volatile.Read(ref *methodTokenAddress);
            if (!ComAbi.TryQueryInterface(
                oldFunction,
                ICorDebugFunction2Abi.InterfaceId,
                out function2))
            {
                return false;
            }

            uint methodVersion = 0;
            uint* methodVersionAddress = &methodVersion;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugFunction2Abi(function2).GetVersionNumber(
                    (nint)methodVersionAddress),
                "ICorDebugFunction2.GetVersionNumber");
            methodVersion = Volatile.Read(ref *methodVersionAddress);
            nint* moduleAddress = &module;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugFunctionAbi(oldFunction).GetModule((nint)moduleAddress),
                "ICorDebugFunction.GetModule");
            module = Volatile.Read(ref *moduleAddress);
            CorDebugLoadedModule? loadedModule = module == 0 ? null : FindModule(module);
            return loadedModule is not null && _hotReloadRemaps.TryGetValue(
                (
                    loadedModule.Identity,
                    methodToken,
                    checked((int)methodVersion),
                    oldIlOffset),
                out newIlOffset);
        }
        finally
        {
            if (module != 0)
            {
                _ = ComAbi.Release(module);
            }

            if (function2 != 0)
            {
                _ = ComAbi.Release(function2);
            }
        }
    }

    private void RemoveHotReloadRemaps(nint moduleIdentity)
    {
        foreach ((nint identity, uint token, int version, uint offset) in
            _hotReloadRemaps.Keys.Where(key => key.ModuleIdentity == moduleIdentity).ToArray())
        {
            _ = _hotReloadRemaps.Remove((identity, token, version, offset));
        }
    }
}
