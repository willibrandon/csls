using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Resolves managed-symbol locations and binds them to runtime breakpoints.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
    private async ValueTask BindModuleAsync(
        CorDebugLoadedModule module,
        IReadOnlyList<SourceBreakpointDefinition> definitions,
        bool notifyChanges,
        CancellationToken cancellationToken)
    {
        Dictionary<int, SourceBreakpointLocation> locations;
        Dictionary<string, string> sourceFailures;
        try
        {
            using DebugSymbolReader? symbols = OpenSymbols(module);
            module.SymbolsInspected = true;
            if (symbols is null)
            {
                return;
            }

            module.SymbolKind = symbols.StorageKind switch
            {
                DebugSymbolStorageKind.Embedded => DebugModuleSymbolKind.EmbeddedPortablePdb,
                DebugSymbolStorageKind.InMemory => DebugModuleSymbolKind.InMemoryPortablePdb,
                DebugSymbolStorageKind.Windows => DebugModuleSymbolKind.WindowsPdb,
                _ => DebugModuleSymbolKind.PortablePdb
            };
            module.SymbolPath = symbols.Path;

            locations = ResolveLocations(symbols.GetSequencePoints(methodToken: null), definitions, cancellationToken);
            sourceFailures = GetSourceValidationFailures(symbols, definitions);
        }
        catch (Exception exception) when (DebugSymbolReader.IsReadFailure(exception))
        {
            module.SymbolsInspected = true;
            return;
        }

        foreach (SourceBreakpointDefinition definition in definitions)
        {
            string? previousMessage = definition.ToInfo().Message;
            _ = definition.BindingFailures.Remove(module.Id);
            if (definition.ValidationMessage is not null)
            {
                continue;
            }

            if (!locations.TryGetValue(definition.Id, out SourceBreakpointLocation? location))
            {
                continue;
            }

            if (sourceFailures.TryGetValue(definition.SourcePath, out string? failure))
            {
                definition.BindingFailures[module.Id] = failure;
                if (notifyChanges && definition.ResolvedLine is null &&
                    !string.Equals(previousMessage, failure, StringComparison.Ordinal))
                {
                    await _notifyChanged(definition.ToInfo(), cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            if (!TryBind(module, definition, location))
            {
                await ReportBindingFailureAsync(module, definition, notifyChanges, cancellationToken).ConfigureAwait(false);
                continue;
            }

            bool firstResolution = definition.ResolvedLine is null;
            definition.ResolvedLine = location.Line;
            definition.ResolvedColumn = location.Column;
            if (notifyChanges && firstResolution)
            {
                await _notifyChanged(definition.ToInfo(), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private Dictionary<string, string> GetSourceValidationFailures(
        DebugSymbolReader symbols,
        IReadOnlyList<SourceBreakpointDefinition> definitions)
    {
        var failures = new Dictionary<string, string>(PathComparer);
        if (!_requireExactSource)
        {
            return failures;
        }

        var requestedPaths = new HashSet<string>(definitions.Select(static definition => definition.SourcePath), PathComparer);
        foreach (ManagedSymbolDocument document in symbols.GetDocuments())
        {
            string path = _sourcePathMapper.Map(document.Path);
            if (!requestedPaths.Contains(path))
            {
                continue;
            }

            LocalSourceStatus status = SourceChecksumVerifier.InspectFile(path, document.Checksum);
            if (status == LocalSourceStatus.Mismatch)
            {
                failures[path] = "The source file differs from the loaded debug symbols. " +
                    "Rebuild the target or set requireExactSource to false to bind using this source.";
            }
            else if (status == LocalSourceStatus.Unverified)
            {
                failures[path] = "The source file has no supported checksum in the loaded debug symbols. " +
                    "Set requireExactSource to false to bind using this source.";
            }
        }

        return failures;
    }

    private unsafe bool TryBind(
        CorDebugLoadedModule module,
        SourceBreakpointDefinition definition,
        SourceBreakpointLocation location)
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
                    location.MethodToken,
                    (nint)functionAddress),
                "ICorDebugModule.GetFunctionFromToken");
            function = Volatile.Read(ref *functionAddress);
            nint* codeAddress = &code;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugFunctionAbi(function).GetILCode((nint)codeAddress),
                "ICorDebugFunction.GetILCode");
            code = Volatile.Read(ref *codeAddress);
            nint* breakpointAddress = &breakpoint;
            int result = new ICorDebugCodeAbi(code).CreateBreakpoint(
                location.IlOffset,
                (nint)breakpointAddress);
            breakpoint = Volatile.Read(ref *breakpointAddress);
            if (result == UnableToSetBreakpointHResult)
            {
                return false;
            }

            CorDebugHResult.ThrowIfFailed(result, "ICorDebugCode.CreateBreakpoint");
            result = new ICorDebugBreakpointAbi(breakpoint).Activate(bActive: 1);
            if (result == UnableToSetBreakpointHResult)
            {
                return false;
            }

            CorDebugHResult.ThrowIfFailed(result, "ICorDebugBreakpoint.Activate");
            identity = ComAbi.QueryInterface(breakpoint, s_iUnknownInterfaceId);
            _bindings.Add(identity, new SourceBreakpointBinding
            {
                BreakpointId = definition.Id,
                Definition = definition,
                ModuleIdentity = module.Identity,
                Breakpoint = breakpoint,
                Identity = identity
            });
            breakpoint = 0;
            identity = 0;
            return true;
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

    private void RemoveBindings(string sourcePath)
    {
        if (!_definitions.TryGetValue(sourcePath, out List<SourceBreakpointDefinition>? definitions))
        {
            return;
        }

        var ids = definitions.Select(static definition => definition.Id).ToHashSet();
        foreach ((nint identity, SourceBreakpointBinding binding) in _bindings.ToArray())
        {
            if (!ids.Contains(binding.BreakpointId))
            {
                continue;
            }

            _ = new ICorDebugBreakpointAbi(binding.Breakpoint).Activate(bActive: 0);
            _ = ComAbi.Release(binding.Identity);
            _ = ComAbi.Release(binding.Breakpoint);
            _ = _bindings.Remove(identity);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        DebuggerPathIdentity.AreEquivalent(left, right);
}
