using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Owns the one-shot entry breakpoint for a managed launch on the session actor.
/// </summary>
internal sealed class EntryPointBreakpointManager : IDisposable
{
    private static readonly Guid s_iUnknownInterfaceId = new("00000000-0000-0000-C000-000000000046");
    private nint _breakpoint;
    private nint _identity;
    private int _moduleId;
    private bool _enabled;

    /// <summary>
    /// Configures entry stopping for the next target activation.
    /// </summary>
    /// <param name="enabled">Whether this launch should stop at its entry point.</param>
    internal void Configure(bool enabled)
    {
        Reset();
        _enabled = enabled;
    }

    /// <summary>
    /// Installs an entry breakpoint while the executable module is suspended during loading.
    /// </summary>
    /// <param name="module">The module with its validated symbols.</param>
    /// <param name="cancellationToken">Cancels entry resolution.</param>
    internal void LoadModule(CorDebugLoadedModule module, CancellationToken cancellationToken)
    {
        if (!_enabled || _breakpoint != 0)
        {
            return;
        }

        (uint MethodToken, uint IlOffset)? location = EntryPointLocationResolver.Resolve(module, cancellationToken);
        if (location is null)
        {
            return;
        }

        Bind(module, location.Value.MethodToken, location.Value.IlOffset);
    }

    /// <summary>
    /// Releases an entry binding whose module is unloading.
    /// </summary>
    /// <param name="moduleId">The session-local module identifier.</param>
    internal void UnloadModule(int moduleId)
    {
        if (_moduleId == moduleId)
        {
            ReleaseBinding(runtimeAvailable: true);
        }
    }

    /// <summary>
    /// Recognizes and retires the entry breakpoint before publishing its stop.
    /// </summary>
    /// <param name="breakpoint">The borrowed runtime breakpoint callback pointer.</param>
    /// <returns>True when this is the launch's first entry hit.</returns>
    internal bool TryComplete(nint breakpoint)
    {
        if (_breakpoint == 0)
        {
            return false;
        }

        nint identity = ComAbi.QueryInterface(breakpoint, s_iUnknownInterfaceId);
        try
        {
            if (identity != _identity)
            {
                return false;
            }

            Reset();
            return true;
        }
        finally
        {
            _ = ComAbi.Release(identity);
        }
    }

    /// <summary>
    /// Retires entry policy and native ownership after target shutdown or failed activation.
    /// </summary>
    /// <param name="runtimeAvailable">Whether the runtime accepts breakpoint deactivation.</param>
    internal void Reset(bool runtimeAvailable = true)
    {
        _enabled = false;
        ReleaseBinding(runtimeAvailable);
    }

    /// <inheritdoc />
    public void Dispose() => Reset();

    private unsafe void Bind(CorDebugLoadedModule module, uint methodToken, uint ilOffset)
    {
        nint function = 0;
        nint code = 0;
        nint breakpoint = 0;
        nint identity = 0;
        try
        {
            nint* functionAddress = &function;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugModuleAbi(module.Pointer).GetFunctionFromToken(methodToken, (nint)functionAddress),
                "ICorDebugModule.GetFunctionFromToken");
            function = Volatile.Read(ref *functionAddress);
            nint* codeAddress = &code;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugFunctionAbi(function).GetILCode((nint)codeAddress),
                "ICorDebugFunction.GetILCode");
            code = Volatile.Read(ref *codeAddress);
            nint* breakpointAddress = &breakpoint;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugCodeAbi(code).CreateBreakpoint(ilOffset, (nint)breakpointAddress),
                "ICorDebugCode.CreateBreakpoint");
            breakpoint = Volatile.Read(ref *breakpointAddress);
            identity = ComAbi.QueryInterface(breakpoint, s_iUnknownInterfaceId);
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugBreakpointAbi(breakpoint).Activate(bActive: 1),
                "ICorDebugBreakpoint.Activate");
            _breakpoint = breakpoint;
            _identity = identity;
            _moduleId = module.Id;
            breakpoint = 0;
            identity = 0;
        }
        finally
        {
            if (breakpoint != 0)
            {
                _ = new ICorDebugBreakpointAbi(breakpoint).Activate(bActive: 0);
            }

            Release(identity);
            Release(breakpoint);
            Release(code);
            Release(function);
        }
    }

    private void ReleaseBinding(bool runtimeAvailable)
    {
        if (_breakpoint != 0 && runtimeAvailable)
        {
            _ = new ICorDebugBreakpointAbi(_breakpoint).Activate(bActive: 0);
        }

        Release(_identity);
        Release(_breakpoint);
        _identity = 0;
        _breakpoint = 0;
        _moduleId = 0;
    }

    private static void Release(nint value)
    {
        if (value != 0)
        {
            _ = ComAbi.Release(value);
        }
    }
}
