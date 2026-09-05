using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Classifies user modules and installs CoreCLR Just My Code stepping policy.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
    /// <summary>
    /// Installs module JMC status before the first source step.
    /// </summary>
    internal void ActivateSteppingPolicy()
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

    private unsafe void ConfigureJustMyCode(CorDebugLoadedModule module)
    {
        ClassifyUserCode(module);
        module.JustMyCodeConfigured = true;
        if (module.IsUserCode != true)
        {
            _ = SetModuleJustMyCode(module, isUserCode: false);
            return;
        }

        if (!SetModuleJustMyCode(module, isUserCode: true))
        {
            return;
        }

        try
        {
            using PEReader? peReader = module.OpenPeReader();
            if (peReader is null)
            {
                module.IsUserCode = false;
                module.JustMyCodeDiagnostic =
                    "Step-filter metadata is unavailable because the module image cannot be read.";
                return;
            }

            uint[] excludedTokens = ManagedStepFilterClassifier.GetExcludedTokens(
                peReader,
                _justMyCode,
                _enableStepFiltering);
            ApplyStepFilters(module, excludedTokens);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                BadImageFormatException or InvalidOperationException or OverflowException)
        {
            module.IsUserCode = false;
            module.JustMyCodeDiagnostic =
                $"Step-filter metadata is unavailable: {exception.Message}";
        }
    }

    private static bool SetModuleJustMyCode(
        CorDebugLoadedModule module,
        bool isUserCode)
    {
        if (!ComAbi.TryQueryInterface(
            module.Pointer,
            ICorDebugModule2Abi.InterfaceId,
            out nint module2))
        {
            module.IsUserCode = false;
            module.JustMyCodeDiagnostic =
                "The runtime does not expose Just My Code module policy.";
            return false;
        }

        try
        {
            int result = new ICorDebugModule2Abi(module2).SetJMCStatus(
                isUserCode ? 1 : 0,
                cTokens: 0,
                pTokens: 0);
            if (result >= 0)
            {
                return true;
            }

            module.IsUserCode = false;
            module.JustMyCodeDiagnostic =
                $"Just My Code configuration failed with HRESULT 0x{result:X8}.";
            return false;
        }
        finally
        {
            _ = ComAbi.Release(module2);
        }
    }
}
