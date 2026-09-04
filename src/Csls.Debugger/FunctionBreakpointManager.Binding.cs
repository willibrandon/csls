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

            MetadataReader metadata = peReader.GetMetadataReader();
            foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
            {
                TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
                string typeName = GetTypeName(metadata, typeHandle);
                foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                {
                    string methodName = metadata.GetString(
                        metadata.GetMethodDefinition(methodHandle).Name);
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

    private static string GetTypeName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        TypeDefinition type = metadata.GetTypeDefinition(handle);
        string name = metadata.GetString(type.Name);
        TypeDefinitionHandle declaringType = type.GetDeclaringType();
        if (!declaringType.IsNil)
        {
            return $"{GetTypeName(metadata, declaringType)}.{name}";
        }

        string @namespace = metadata.GetString(type.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
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
