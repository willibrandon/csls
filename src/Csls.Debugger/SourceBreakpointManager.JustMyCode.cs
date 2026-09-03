using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Classifies user modules and installs CoreCLR Just My Code stepping policy.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
    /// <summary>
    /// Installs module JMC status before the first source step.
    /// </summary>
    /// <returns>Whether the runtime stepper should exclude non-user code.</returns>
    internal bool ActivateSteppingPolicy()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!_steppingPolicyActivated)
        {
            foreach (CorDebugLoadedModule module in _modules.Values)
            {
                ConfigureJustMyCode(module);
            }

            _steppingPolicyActivated = true;
        }

        return _justMyCode;
    }

    private void ClassifyUserCode(CorDebugLoadedModule module)
    {
        if (module.JustMyCodeConfigured)
        {
            return;
        }

        EnsureSymbolsInspected(module);
        module.IsUserCode = module.SymbolKind != DebugModuleSymbolKind.None &&
            (!_justMyCode || module.IsOptimized == false);
    }

    private void ConfigureJustMyCode(CorDebugLoadedModule module)
    {
        ClassifyUserCode(module);
        module.JustMyCodeConfigured = true;
        if (!_justMyCode)
        {
            return;
        }

        if (!ComAbi.TryQueryInterface(
            module.Pointer,
            ICorDebugModule2Abi.InterfaceId,
            out nint module2))
        {
            module.IsUserCode = false;
            module.JustMyCodeDiagnostic =
                "The runtime does not expose Just My Code module policy.";
            return;
        }

        try
        {
            int result = new ICorDebugModule2Abi(module2).SetJMCStatus(
                module.IsUserCode == true ? 1 : 0,
                cTokens: 0,
                pTokens: 0);
            if (result < 0)
            {
                module.IsUserCode = false;
                module.JustMyCodeDiagnostic =
                    $"Just My Code configuration failed with HRESULT 0x{result:X8}.";
            }
        }
        finally
        {
            _ = ComAbi.Release(module2);
        }
    }
}
