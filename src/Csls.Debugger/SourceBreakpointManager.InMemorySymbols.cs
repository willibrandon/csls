using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Replaces runtime-supplied in-memory Portable PDBs and rebinds source breakpoints.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
    /// <summary>
    /// Applies one complete in-memory symbol update while CoreCLR is stopped.
    /// </summary>
    /// <param name="module">The borrowed module that owns the symbol stream.</param>
    /// <param name="symbolImage">The complete bounded symbol stream image.</param>
    /// <param name="cancellationToken">Cancels breakpoint-change notifications.</param>
    /// <returns>A task completed after symbol validation and breakpoint rebinding.</returns>
    internal async ValueTask UpdateModuleSymbolsAsync(
        nint module,
        byte[] symbolImage,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfZero(module);
        ArgumentNullException.ThrowIfNull(symbolImage);
        using var symbols = PortablePdbReader.TryOpen(symbolImage);
        if (symbols is null)
        {
            return;
        }

        nint identity = ComAbi.QueryInterface(module, s_iUnknownInterfaceId);
        try
        {
            if (!_modules.TryGetValue(identity, out CorDebugLoadedModule? loadedModule))
            {
                return;
            }

            await ApplyInMemorySymbolsAsync(
                loadedModule,
                symbolImage,
                notifyChanges: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = ComAbi.Release(identity);
        }
    }

    /// <summary>
    /// Refreshes a dynamic module after CoreCLR reports newly defined classes.
    /// </summary>
    /// <param name="module">The borrowed module whose symbols may have changed.</param>
    /// <param name="cancellationToken">Cancels breakpoint-change notifications.</param>
    /// <returns>A task completed after any available symbol snapshot is applied.</returns>
    internal async ValueTask RefreshInMemorySymbolsAsync(
        nint module,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfZero(module);
        nint identity = ComAbi.QueryInterface(module, s_iUnknownInterfaceId);
        try
        {
            if (_modules.TryGetValue(identity, out CorDebugLoadedModule? loadedModule))
            {
                await RefreshInMemorySymbolsAsync(
                    loadedModule,
                    notifyChanges: true,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _ = ComAbi.Release(identity);
        }
    }

    private async ValueTask RefreshInMemorySymbolsAsync(
        CorDebugLoadedModule module,
        bool notifyChanges,
        CancellationToken cancellationToken)
    {
        if (!module.IsInMemory && !module.IsDynamic)
        {
            return;
        }

        byte[]? symbolImage = CorDebugInMemorySymbolReader.TryRead(module.Pointer);
        using PortablePdbReader? symbols = symbolImage is null
            ? null
            : PortablePdbReader.TryOpen(symbolImage);
        if (symbols is null)
        {
            return;
        }

        await ApplyInMemorySymbolsAsync(
            module,
            symbolImage!,
            notifyChanges,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ApplyInMemorySymbolsAsync(
        CorDebugLoadedModule module,
        byte[] symbolImage,
        bool notifyChanges,
        CancellationToken cancellationToken)
    {
        RemoveModuleBindings(module.Identity);
        module.SymbolImage = symbolImage;
        module.SymbolDeltas.Clear();
        module.SymbolKind = DebugModuleSymbolKind.InMemoryPortablePdb;
        module.SymbolPath = null;
        module.SymbolsInspected = true;
        module.IsUserCode = null;
        module.JustMyCodeConfigured = false;
        ClearSources();
        if (_steppingPolicyActivated)
        {
            ConfigureJustMyCode(module);
        }

        foreach (List<SourceBreakpointDefinition> definitions in _definitions.Values)
        {
            await BindModuleAsync(
                module,
                definitions,
                notifyChanges,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private void RemoveModuleBindings(nint moduleIdentity)
    {
        foreach ((nint breakpointIdentity, SourceBreakpointBinding binding) in
            _bindings.ToArray())
        {
            if (binding.ModuleIdentity != moduleIdentity)
            {
                continue;
            }

            _ = new ICorDebugBreakpointAbi(binding.Breakpoint).Activate(bActive: 0);
            _ = ComAbi.Release(binding.Identity);
            _ = ComAbi.Release(binding.Breakpoint);
            _ = _bindings.Remove(breakpointIdentity);
        }
    }
}
