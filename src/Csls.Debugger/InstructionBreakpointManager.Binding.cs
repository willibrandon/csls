using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Creates and releases managed-IL runtime breakpoint bindings.
/// </summary>
internal sealed partial class InstructionBreakpointManager
{
    private async ValueTask BindModuleAsync(
        InstructionBreakpointModule module,
        bool notifyChanges,
        CancellationToken cancellationToken)
    {
        foreach (InstructionBreakpointDefinition definition in _definitions)
        {
            if (definition.ValidationMessage is not null ||
                !MatchesModule(module, definition))
            {
                continue;
            }

            bool firstBinding = definition.BindingCount == 0;
            try
            {
                Bind(module, definition);
                definition.BindingCount++;
                definition.BindingMessage = null;
            }
            catch (InvalidOperationException exception)
            {
                if (definition.BindingCount == 0)
                {
                    definition.BindingMessage = exception.Message;
                }
            }

            if (notifyChanges && firstBinding)
            {
                await _notifyChanged(definition.ToInfo(), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static bool MatchesModule(
        InstructionBreakpointModule module,
        InstructionBreakpointDefinition definition) =>
        definition.ModulePath is not null && module.Path is not null
            ? PathsEqual(module.Path, definition.ModulePath)
            : definition.ModuleId is not null && definition.ModuleId == module.Id;

    private unsafe void Bind(
        InstructionBreakpointModule module,
        InstructionBreakpointDefinition definition)
    {
        nint function = 0;
        nint code = 0;
        nint breakpoint = 0;
        nint identity = 0;
        try
        {
            nint* functionAddress = &function;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugModuleAbi(module.Pointer).GetFunctionFromToken(
                    definition.MethodToken,
                    (nint)functionAddress),
                "ICorDebugModule.GetFunctionFromToken");
            function = Volatile.Read(ref *functionAddress);
            nint* codeAddress = &code;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugFunctionAbi(function).GetILCode((nint)codeAddress),
                "ICorDebugFunction.GetILCode");
            code = Volatile.Read(ref *codeAddress);
            nint* breakpointAddress = &breakpoint;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugCodeAbi(code).CreateBreakpoint(
                    definition.IlOffset,
                    (nint)breakpointAddress),
                "ICorDebugCode.CreateBreakpoint");
            breakpoint = Volatile.Read(ref *breakpointAddress);
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugBreakpointAbi(breakpoint).Activate(bActive: 1),
                "ICorDebugBreakpoint.Activate");
            identity = ComAbi.QueryInterface(breakpoint, s_iUnknownInterfaceId);
            _bindings.Add(identity, new InstructionBreakpointBinding
            {
                Definition = definition,
                ModuleIdentity = module.Identity,
                Breakpoint = breakpoint,
                Identity = identity
            });
            breakpoint = 0;
            identity = 0;
        }
        finally
        {
            if (identity != 0)
            {
                _ = ComAbi.Release(identity);
            }

            if (breakpoint != 0)
            {
                _ = new ICorDebugBreakpointAbi(breakpoint).Activate(bActive: 0);
                _ = ComAbi.Release(breakpoint);
            }

            if (code != 0)
            {
                _ = ComAbi.Release(code);
            }

            if (function != 0)
            {
                _ = ComAbi.Release(function);
            }
        }
    }

    private void ReleaseBindings(bool runtimeAvailable = true)
    {
        foreach (InstructionBreakpointBinding binding in _bindings.Values)
        {
            ReleaseBinding(binding, runtimeAvailable);
            binding.Definition.BindingCount--;
        }

        _bindings.Clear();
    }

    private static void ReleaseBinding(
        InstructionBreakpointBinding binding,
        bool runtimeAvailable = true)
    {
        if (runtimeAvailable)
        {
            _ = new ICorDebugBreakpointAbi(binding.Breakpoint).Activate(bActive: 0);
        }

        _ = ComAbi.Release(binding.Identity);
        _ = ComAbi.Release(binding.Breakpoint);
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
