using Csls.Debugger.Interop;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves bounded proxy property getters from exact loaded runtime metadata.
/// </summary>
internal sealed class ManagedDebuggerTypeProxyPropertyResolver
{
    private const int MaximumPropertyCount = 256;
    private const int MaximumTypeHierarchyDepth = 256;
    private readonly SourceBreakpointManager _modules;

    /// <summary>
    /// Creates a property resolver over the current loaded runtime-module catalog.
    /// </summary>
    /// <param name="modules">The current loaded runtime-module catalog.</param>
    internal ManagedDebuggerTypeProxyPropertyResolver(SourceBreakpointManager modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _modules = modules;
    }

    /// <summary>
    /// Resolves visible non-indexed instance property getters for one constructed proxy.
    /// </summary>
    /// <param name="value">The retained constructed proxy value.</param>
    /// <returns>Owned getter bindings in ordinal debugger display order.</returns>
    internal unsafe List<ManagedDebuggerTypeProxyPropertyBinding> Resolve(nint value)
    {
        nint value2 = 0;
        nint currentType = 0;
        List<ManagedDebuggerTypeProxyPropertyBinding> result = [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            value2 = ComAbi.QueryInterface(value, ICorDebugValue2Abi.InterfaceId);
            nint* exactTypeAddress = &currentType;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugValue2Abi(value2).GetExactType((nint)exactTypeAddress),
                "ICorDebugValue2.GetExactType");
            currentType = RequirePointer(
                Volatile.Read(ref *exactTypeAddress),
                "ICorDebugValue2.GetExactType");

            for (int depth = 0;
                currentType != 0 && depth < MaximumTypeHierarchyDepth;
                depth++)
            {
                nint runtimeClass = 0;
                nint module = 0;
                nint baseType = 0;
                try
                {
                    runtimeClass = GetRuntimeTypeClass(currentType);
                    module = GetClassModule(runtimeClass);
                    uint typeToken = GetClassToken(runtimeClass);
                    using PEReader peReader = OpenRuntimeModule(module);
                    AddDeclaredProperties(
                        result,
                        names,
                        module,
                        currentType,
                        peReader.GetMetadataReader(),
                        typeToken);

                    nint* baseTypeAddress = &baseType;
                    CorDebugHResult.ThrowIfFailed(
                        new ICorDebugTypeAbi(currentType).GetBase((nint)baseTypeAddress),
                        "ICorDebugType.GetBase");
                    baseType = Volatile.Read(ref *baseTypeAddress);
                }
                finally
                {
                    if (module != 0)
                    {
                        _ = ComAbi.Release(module);
                    }

                    if (runtimeClass != 0)
                    {
                        _ = ComAbi.Release(runtimeClass);
                    }

                    if (currentType != 0)
                    {
                        _ = ComAbi.Release(currentType);
                    }

                    currentType = baseType;
                }
            }

            if (currentType != 0)
            {
                throw new InvalidOperationException(
                    $"The proxy type hierarchy exceeds the supported depth of " +
                    $"{MaximumTypeHierarchyDepth}.");
            }

            result.Sort(static (left, right) => string.Compare(
                left.Name,
                right.Name,
                StringComparison.Ordinal));
            return result;
        }
        catch
        {
            ReleaseAll(result);
            throw;
        }
        finally
        {
            if (currentType != 0)
            {
                _ = ComAbi.Release(currentType);
            }

            if (value2 != 0)
            {
                _ = ComAbi.Release(value2);
            }
        }
    }

    private static void AddDeclaredProperties(
        List<ManagedDebuggerTypeProxyPropertyBinding> result,
        HashSet<string> names,
        nint module,
        nint declaringType,
        MetadataReader metadata,
        uint typeToken)
    {
        TypeDefinition type = metadata.GetTypeDefinition(
            MetadataTokens.TypeDefinitionHandle(checked((int)(typeToken & 0x00FFFFFF))));
        foreach (PropertyDefinitionHandle propertyHandle in type.GetProperties())
        {
            PropertyDefinition property = metadata.GetPropertyDefinition(propertyHandle);
            MethodDefinitionHandle getterHandle = property.GetAccessors().Getter;
            if (getterHandle.IsNil)
            {
                continue;
            }

            MethodDefinition getter = metadata.GetMethodDefinition(getterHandle);
            MethodSignature<string> signature = getter.DecodeSignature(
                FunctionEvaluationSignatureTypeProvider.Instance,
                genericContext: null);
            bool hasBrowsingState = ManagedDebuggerAttributeReader.TryGetBrowsableState(
                metadata,
                property.GetCustomAttributes(),
                out ManagedDebuggerBrowsableState browsingState);
            if ((getter.Attributes & MethodAttributes.Abstract) != 0 ||
                signature.Header.IsGeneric ||
                signature.ParameterTypes.Length != 0 ||
                browsingState == ManagedDebuggerBrowsableState.Never ||
                !IsVisibleProxyGetter(getter.Attributes) && !hasBrowsingState)
            {
                continue;
            }

            string name = metadata.GetString(property.Name);
            if (!names.Add(name))
            {
                continue;
            }

            if (result.Count >= MaximumPropertyCount)
            {
                throw new InvalidOperationException(
                    $"The debugger proxy exceeds the property limit of " +
                    $"{MaximumPropertyCount}.");
            }

            result.Add(new ManagedDebuggerTypeProxyPropertyBinding(
                name,
                signature.ReturnType,
                browsingState,
                (getter.Attributes & MethodAttributes.Static) != 0,
                GetModuleFunction(
                    module,
                    checked((uint)MetadataTokens.GetToken(getterHandle))),
                declaringType));
        }
    }

    private PEReader OpenRuntimeModule(nint module) => _modules
        .FindModule(module)
        ?.OpenPeReader() ?? new PEReader(new FileStream(
            CorDebugModulePath.Get(module),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete));

    private static bool IsVisibleProxyGetter(MethodAttributes attributes)
    {
        MethodAttributes accessibility = attributes & MethodAttributes.MemberAccessMask;
        return accessibility is MethodAttributes.Public or
            MethodAttributes.Family or
            MethodAttributes.FamORAssem;
    }

    private static unsafe nint GetRuntimeTypeClass(nint type)
    {
        nint runtimeClass = 0;
        nint* runtimeClassAddress = &runtimeClass;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugTypeAbi(type).GetClass((nint)runtimeClassAddress),
            "ICorDebugType.GetClass");
        return RequirePointer(
            Volatile.Read(ref *runtimeClassAddress),
            "ICorDebugType.GetClass");
    }

    private static unsafe nint GetClassModule(nint runtimeClass)
    {
        nint module = 0;
        nint* moduleAddress = &module;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugClassAbi(runtimeClass).GetModule((nint)moduleAddress),
            "ICorDebugClass.GetModule");
        return RequirePointer(
            Volatile.Read(ref *moduleAddress),
            "ICorDebugClass.GetModule");
    }

    private static unsafe uint GetClassToken(nint runtimeClass)
    {
        uint token = 0;
        uint* tokenAddress = &token;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugClassAbi(runtimeClass).GetToken((nint)tokenAddress),
            "ICorDebugClass.GetToken");
        return Volatile.Read(ref *tokenAddress);
    }

    private static unsafe nint GetModuleFunction(nint module, uint methodToken)
    {
        nint function = 0;
        nint* functionAddress = &function;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugModuleAbi(module).GetFunctionFromToken(
                methodToken,
                (nint)functionAddress),
            "ICorDebugModule.GetFunctionFromToken");
        return RequirePointer(
            Volatile.Read(ref *functionAddress),
            "ICorDebugModule.GetFunctionFromToken");
    }

    private static nint RequirePointer(nint pointer, string operation) => pointer != 0
        ? pointer
        : throw new InvalidOperationException($"{operation} returned a null pointer.");

    private static void ReleaseAll(
        IEnumerable<ManagedDebuggerTypeProxyPropertyBinding> bindings)
    {
        foreach (ManagedDebuggerTypeProxyPropertyBinding binding in bindings)
        {
            binding.Release();
        }
    }
}
