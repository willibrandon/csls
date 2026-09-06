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

    /// <summary>
    /// Resolves a stopped method's classification from module policy and runtime method overrides.
    /// </summary>
    /// <param name="module">The actor-owned loaded module containing the method.</param>
    /// <param name="function">The borrowed ICorDebugFunction pointer.</param>
    /// <returns>The known user-code classification, or null when the runtime query fails.</returns>
    internal unsafe bool? GetUserCodeStatus(CorDebugLoadedModule module, nint function)
    {
        ClassifyUserCode(module);
        if (module.IsUserCode != true || !module.JustMyCodeConfigured)
        {
            return module.IsUserCode;
        }

        if (!ComAbi.TryQueryInterface(function, ICorDebugFunction2Abi.InterfaceId, out nint function2))
        {
            return null;
        }

        try
        {
            int isUserCode = 0;
            int* address = &isUserCode;
            int result = new ICorDebugFunction2Abi(function2).GetJMCStatus((nint)address);
            isUserCode = Volatile.Read(ref *address);
            return result >= 0 ? isUserCode != 0 : null;
        }
        finally
        {
            _ = ComAbi.Release(function2);
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
