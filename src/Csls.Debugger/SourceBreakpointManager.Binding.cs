using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Csls.Debugger;

/// <summary>
/// Resolves Portable PDB locations and binds them to runtime breakpoints.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
    private async ValueTask BindModuleAsync(
        CorDebugLoadedModule module,
        IReadOnlyList<SourceBreakpointDefinition> definitions,
        bool notifyChanges,
        CancellationToken cancellationToken)
    {
        string? modulePath = module.Path;
        if (modulePath is null)
        {
            return;
        }

        using var symbols = PortablePdbReader.TryOpen(modulePath);
        if (symbols is null)
        {
            return;
        }

        Dictionary<int, SourceBreakpointLocation> locations;
        try
        {
            locations = ResolveLocations(symbols.Metadata, definitions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return;
        }

        foreach (SourceBreakpointDefinition definition in definitions)
        {
            if (!locations.TryGetValue(definition.Id, out SourceBreakpointLocation? location))
            {
                continue;
            }

            Bind(module, definition, location);
            bool firstResolution = definition.ResolvedLine is null;
            definition.ResolvedLine = location.Line;
            definition.ResolvedColumn = location.Column;
            if (notifyChanges && firstResolution)
            {
                await _notifyChanged(definition.ToInfo(), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static Dictionary<int, SourceBreakpointLocation> ResolveLocations(
        MetadataReader reader,
        IReadOnlyList<SourceBreakpointDefinition> definitions)
    {
        var result = new Dictionary<int, SourceBreakpointLocation>();
        int rowNumber = 0;
        foreach (MethodDebugInformationHandle handle in reader.MethodDebugInformation)
        {
            rowNumber++;
            MethodDebugInformation method = reader.GetMethodDebugInformation(handle);
            foreach (SequencePoint point in method.GetSequencePoints())
            {
                if (point.IsHidden || point.StartLine == HiddenSequencePointLine)
                {
                    continue;
                }

                DocumentHandle documentHandle = point.Document.IsNil ? method.Document : point.Document;
                if (documentHandle.IsNil)
                {
                    continue;
                }

                string documentPath = Path.GetFullPath(
                    reader.GetString(reader.GetDocument(documentHandle).Name));
                foreach (SourceBreakpointDefinition definition in definitions)
                {
                    if (!PathsEqual(documentPath, definition.SourcePath) ||
                        !IsBetterLocation(definition, point, result))
                    {
                        continue;
                    }

                    result[definition.Id] = new SourceBreakpointLocation(
                        checked((uint)MetadataTokens.GetToken(
                            MetadataTokens.MethodDefinitionHandle(rowNumber))),
                        checked((uint)point.Offset),
                        point.StartLine,
                        point.StartColumn,
                        point.EndLine);
                }
            }
        }

        return result;
    }

    private static bool IsBetterLocation(
        SourceBreakpointDefinition definition,
        SequencePoint candidate,
        Dictionary<int, SourceBreakpointLocation> current)
    {
        bool candidateContainsLine = definition.RequestedLine >= candidate.StartLine &&
            definition.RequestedLine <= candidate.EndLine;
        if (!candidateContainsLine && candidate.StartLine < definition.RequestedLine)
        {
            return false;
        }

        if (!current.TryGetValue(definition.Id, out SourceBreakpointLocation? existing))
        {
            return true;
        }

        bool existingContainsLine = definition.RequestedLine >= existing.Line &&
            definition.RequestedLine <= existing.EndLine;
        if (candidateContainsLine != existingContainsLine)
        {
            return candidateContainsLine;
        }

        int candidateDistance = Math.Abs(candidate.StartLine - definition.RequestedLine);
        int existingDistance = Math.Abs(existing.Line - definition.RequestedLine);
        if (candidateDistance != existingDistance)
        {
            return candidateDistance < existingDistance;
        }

        int requestedColumn = definition.RequestedColumn ?? 0;
        return Math.Abs(candidate.StartColumn - requestedColumn) <
            Math.Abs(existing.Column - requestedColumn);
    }

    private unsafe void Bind(
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
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugCodeAbi(code).CreateBreakpoint(
                    location.IlOffset,
                    (nint)breakpointAddress),
                "ICorDebugCode.CreateBreakpoint");
            breakpoint = Volatile.Read(ref *breakpointAddress);
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugBreakpointAbi(breakpoint).Activate(bActive: 1),
                "ICorDebugBreakpoint.Activate");
            identity = ComAbi.QueryInterface(breakpoint, s_iUnknownInterfaceId);
            _bindings.Add(identity, new SourceBreakpointBinding
            {
                BreakpointId = definition.Id,
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

    private static bool PathsEqual(string left, string right) => string.Equals(
        left,
        right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
