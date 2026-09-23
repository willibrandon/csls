using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves managed metadata names and activates function-entry breakpoints.
/// </summary>
internal sealed partial class FunctionBreakpointManager
{
    private async ValueTask BindModuleAsync(
        CorDebugLoadedModule module,
        bool notifyChanges,
        CancellationToken cancellationToken)
    {
        if (_definitions.Count == 0)
        {
            return;
        }

        var previouslyUnbound = _definitions
            .Where(static definition => definition.BindingCount == 0)
            .Select(static definition => definition.Id)
            .ToHashSet();
        try
        {
            using PEReader? peReader = module.OpenPeReader();
            if (peReader is null)
            {
                return;
            }

            using var metadata = new ManagedMetadataImage(peReader.GetMetadataReader(), module.MetadataDeltas);
            Dictionary<TypeDefinitionHandle, string> typeNames = [];
            foreach (MethodDefinitionHandle methodHandle in metadata.GetMethods())
            {
                cancellationToken.ThrowIfCancellationRequested();
                TypeDefinitionHandle type = metadata.GetDeclaringType(methodHandle);
                if (!typeNames.TryGetValue(type, out string? typeName))
                {
                    typeName = GetTypeName(metadata, type);
                    typeNames.Add(type, typeName);
                }

                string methodName = metadata.GetString(metadata.GetMethodDefinition(methodHandle).Name);
                foreach (FunctionBreakpointDefinition definition in _definitions)
                {
                    if (definition.ValidationMessage is not null ||
                        !Matches(definition.Name, typeName, methodName) ||
                        !TryBind(module, methodHandle, definition))
                    {
                        continue;
                    }

                    definition.BindingCount++;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return;
        }

        if (!notifyChanges)
        {
            return;
        }

        foreach (FunctionBreakpointDefinition definition in _definitions.Where(
            candidate => previouslyUnbound.Contains(candidate.Id) && candidate.BindingCount > 0))
        {
            await _notifyChanged(definition.ToInfo(), cancellationToken).ConfigureAwait(false);
        }
    }

    private unsafe bool TryBind(
        CorDebugLoadedModule module,
        MethodDefinitionHandle methodHandle,
        FunctionBreakpointDefinition definition)
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
                    checked((uint)MetadataTokens.GetToken(methodHandle)),
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
                new ICorDebugCodeAbi(code).CreateBreakpoint(0, (nint)breakpointAddress),
                "ICorDebugCode.CreateBreakpoint");
            breakpoint = Volatile.Read(ref *breakpointAddress);
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugBreakpointAbi(breakpoint).Activate(bActive: 1),
                "ICorDebugBreakpoint.Activate");
            identity = ComAbi.QueryInterface(breakpoint, s_iUnknownInterfaceId);
            _bindings.Add(identity, new FunctionBreakpointBinding
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
        catch (InvalidOperationException)
        {
            return false;
        }
        finally
        {
            Release(identity);
            if (breakpoint != 0)
            {
                _ = new ICorDebugBreakpointAbi(breakpoint).Activate(bActive: 0);
            }

            Release(breakpoint);
            Release(code);
            Release(function);
        }
    }

    private static bool Matches(string requested, string typeName, string methodName) =>
        string.Equals(requested, methodName, StringComparison.Ordinal) ||
        string.Equals(requested, $"{typeName}.{methodName}", StringComparison.Ordinal);

    private static string GetTypeName(ManagedMetadataImage metadata, TypeDefinitionHandle handle)
    {
        List<string> names = [];
        for (int depth = 0; depth < 256; depth++)
        {
            TypeDefinition type = metadata.GetTypeDefinition(handle);
            names.Add(metadata.GetString(type.Name));
            TypeDefinitionHandle declaringType = metadata.GetDeclaringType(handle);
            if (declaringType.IsNil)
            {
                string @namespace = metadata.GetString(type.Namespace);
                if (!string.IsNullOrEmpty(@namespace))
                {
                    names.Add(@namespace);
                }

                names.Reverse();
                return string.Join('.', names);
            }

            handle = declaringType;
        }

        throw new BadImageFormatException("A function breakpoint type exceeds 256 nested levels.");
    }

    private void ReleaseBindings(bool runtimeAvailable = true)
    {
        foreach (FunctionBreakpointBinding binding in _bindings.Values)
        {
            ReleaseBinding(binding, runtimeAvailable);
        }

        _bindings.Clear();
        foreach (FunctionBreakpointDefinition definition in _definitions)
        {
            definition.BindingCount = 0;
        }
    }

    private static void ReleaseBinding(
        FunctionBreakpointBinding binding,
        bool runtimeAvailable = true)
    {
        if (runtimeAvailable)
        {
            _ = new ICorDebugBreakpointAbi(binding.Breakpoint).Activate(bActive: 0);
        }

        _ = ComAbi.Release(binding.Identity);
        _ = ComAbi.Release(binding.Breakpoint);
    }

    private static void Release(nint value)
    {
        if (value != 0)
        {
            _ = ComAbi.Release(value);
        }
    }
}
