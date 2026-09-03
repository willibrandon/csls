using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Applies launch-time module policy and records truthful module diagnostics.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
    private const uint DisableJitOptimization = 0x3;

    /// <summary>
    /// Configures runtime policy that must be applied during subsequent module-load callbacks.
    /// </summary>
    /// <param name="suppressJitOptimizations">Whether symbol-bearing modules request unoptimized code.</param>
    /// <param name="justMyCode">Whether source stepping excludes non-user managed code.</param>
    /// <param name="enableStepFiltering">Whether stepping skips properties and operators.</param>
    internal void SetRuntimeOptions(
        bool suppressJitOptimizations,
        bool justMyCode,
        bool enableStepFiltering)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_modules.Count != 0)
        {
            throw new InvalidOperationException(
                "Debugger runtime options cannot change after modules have loaded.");
        }

        _suppressJitOptimizations = suppressJitOptimizations;
        _justMyCode = justMyCode;
        _enableStepFiltering = enableStepFiltering;
    }

    private static (DebugModuleSymbolKind SymbolKind, string? SymbolPath) GetSymbolInfo(
        PortablePdbResolution? symbols)
    {
        return symbols?.StorageKind switch
        {
            PortablePdbStorageKind.Embedded =>
                (DebugModuleSymbolKind.EmbeddedPortablePdb, null),
            PortablePdbStorageKind.AssociatedFile =>
                (DebugModuleSymbolKind.PortablePdb, symbols.Path),
            PortablePdbStorageKind.InMemory =>
                (DebugModuleSymbolKind.InMemoryPortablePdb, null),
            _ => (DebugModuleSymbolKind.None, null)
        };
    }

    private static unsafe bool IsInMemoryModule(nint module)
    {
        int inMemory = 0;
        int* inMemoryAddress = &inMemory;
        int result = new ICorDebugModuleAbi(module).IsInMemory((nint)inMemoryAddress);
        inMemory = Volatile.Read(ref *inMemoryAddress);
        return result >= 0 && inMemory != 0;
    }

    private static unsafe bool IsDynamicModule(nint module)
    {
        int dynamic = 0;
        int* dynamicAddress = &dynamic;
        int result = new ICorDebugModuleAbi(module).IsDynamic((nint)dynamicAddress);
        dynamic = Volatile.Read(ref *dynamicAddress);
        return result >= 0 && dynamic != 0;
    }

    private static void EnsureSymbolsInspected(CorDebugLoadedModule module)
    {
        if (module.SymbolsInspected)
        {
            return;
        }

        module.SymbolsInspected = true;
    }

    private unsafe (bool? IsOptimized, string? Diagnostic) ConfigureJitPolicy(
        nint module,
        bool hasSymbols)
    {
        if (!ComAbi.TryQueryInterface(module, ICorDebugModule2Abi.InterfaceId, out nint module2))
        {
            return (null, "The runtime does not expose module JIT policy.");
        }

        try
        {
            var api = new ICorDebugModule2Abi(module2);
            int setResult = 0;
            if (_suppressJitOptimizations && hasSymbols)
            {
                setResult = api.SetJITCompilerFlags(DisableJitOptimization);
            }

            uint flags = 0;
            int getResult = api.GetJITCompilerFlags((nint)(&flags));
            string? diagnostic = setResult < 0
                ? $"JIT optimization suppression failed with HRESULT 0x{setResult:X8}."
                : getResult < 0
                    ? $"JIT optimization state is unavailable (HRESULT 0x{getResult:X8})."
                    : null;
            return (
                getResult < 0 ? null : (flags & DisableJitOptimization) != DisableJitOptimization,
                diagnostic);
        }
        finally
        {
            _ = ComAbi.Release(module2);
        }
    }
}
