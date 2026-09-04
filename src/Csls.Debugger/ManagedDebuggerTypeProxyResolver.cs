using Csls.Debugger.Interop;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves debugger type proxies against loaded target metadata and exact runtime types.
/// </summary>
internal sealed class ManagedDebuggerTypeProxyResolver
{
    private const int MaximumHierarchyDepth = 256;
    private const int MaximumRuntimeTypeArgumentCount = 64;
    private const int MaximumTypeScanCount = 1_000_000;
    private readonly SourceBreakpointManager _modules;

    /// <summary>
    /// Creates a proxy resolver over the loaded runtime-module catalog.
    /// </summary>
    /// <param name="modules">The current loaded runtime-module catalog.</param>
    internal ManagedDebuggerTypeProxyResolver(SourceBreakpointManager modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _modules = modules;
    }

    /// <summary>
    /// Tries to resolve a proxy constructor for one dereferenced runtime object.
    /// </summary>
    /// <param name="value">The retained, dereferenced runtime value.</param>
    /// <param name="binding">Receives owned constructor and type pointers.</param>
    /// <returns>True when a valid loaded proxy can be constructed.</returns>
    internal bool TryResolve(
        nint value,
        out ManagedDebuggerTypeProxyBinding? binding)
    {
        binding = null;
        try
        {
            return TryResolveCore(value, out binding);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or BadImageFormatException or OverflowException)
        {
            binding = null;
            return false;
        }
    }

    private unsafe bool TryResolveCore(
        nint value,
        out ManagedDebuggerTypeProxyBinding? binding)
    {
        binding = null;
        nint value2 = 0;
        nint currentType = 0;
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
                currentType != 0 && depth < MaximumHierarchyDepth;
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
                    MetadataReader metadata = peReader.GetMetadataReader();
                    TypeDefinitionHandle typeHandle = MetadataTokens.TypeDefinitionHandle(
                        checked((int)(typeToken & 0x00FFFFFF)));
                    TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
                    string targetTypeName = GetMetadataTypeName(metadata, typeHandle);
                    ManagedDebuggerTypeProxyAttribute? proxy =
                        ManagedDebuggerAttributeReader.GetDeclaredTypeProxy(metadata, type) ??
                        ManagedDebuggerAttributeReader.GetAssemblyTypeProxy(
                            metadata,
                            targetTypeName);
                    if (proxy is not null && TryBindProxy(
                        proxy,
                        currentType,
                        out binding))
                    {
                        return true;
                    }

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
                    $"The runtime type hierarchy exceeds the supported depth of " +
                    $"{MaximumHierarchyDepth}.");
            }

            return false;
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

    private bool TryBindProxy(
        ManagedDebuggerTypeProxyAttribute attribute,
        nint attributedRuntimeType,
        out ManagedDebuggerTypeProxyBinding? binding)
    {
        binding = null;
        if (!ManagedDebuggerTypeProxyNameParser.TryParse(
            attribute.ProxyTypeName,
            out ManagedDebuggerTypeProxyName? proxyName) ||
            proxyName is null ||
            proxyName.IsConstructed ||
            !TryFindLoadedType(proxyName, out CorDebugLoadedModule? module, out uint typeToken))
        {
            return false;
        }

        if (module is null)
        {
            return false;
        }

        PEReader? peReader = module.OpenPeReader();
        using IDisposable? peReaderDisposal = peReader;
        if (peReader is null || !peReader.HasMetadata)
        {
            return false;
        }

        MetadataReader metadata = peReader.GetMetadataReader();
        TypeDefinition type = metadata.GetTypeDefinition(
            MetadataTokens.TypeDefinitionHandle(checked((int)(typeToken & 0x00FFFFFF))));
        uint? constructorToken = TryGetProxyConstructor(metadata, type);
        if (constructorToken is null)
        {
            return false;
        }

        List<nint> targetTypeArguments = EnumerateTypeArguments(attributedRuntimeType);
        int proxyArity = type.GetGenericParameters().Count;
        if (proxyArity != 0 && proxyArity != targetTypeArguments.Count)
        {
            ReleaseAll(targetTypeArguments);
            return false;
        }

        nint function = 0;
        try
        {
            function = GetModuleFunction(module.Pointer, constructorToken.Value);
            nint[] proxyTypeArguments = proxyArity == 0
                ? []
                : [.. targetTypeArguments];
            if (proxyArity == 0)
            {
                ReleaseAll(targetTypeArguments);
            }

            binding = new ManagedDebuggerTypeProxyBinding(function, proxyTypeArguments);
            function = 0;
            return true;
        }
        finally
        {
            if (function != 0)
            {
                _ = ComAbi.Release(function);
            }
        }
    }

    private bool TryFindLoadedType(
        ManagedDebuggerTypeProxyName proxyName,
        out CorDebugLoadedModule? resolvedModule,
        out uint resolvedToken)
    {
        resolvedModule = null;
        resolvedToken = 0;
        int scannedTypes = 0;
        foreach (CorDebugLoadedModule module in _modules.GetRuntimeModules())
        {
            uint? match = TryFindTypeInModule(module, proxyName, ref scannedTypes);
            if (match is null)
            {
                continue;
            }

            if (resolvedModule is not null)
            {
                return false;
            }

            resolvedModule = module;
            resolvedToken = match.Value;
        }

        return resolvedModule is not null;
    }

    private static uint? TryFindTypeInModule(
        CorDebugLoadedModule module,
        ManagedDebuggerTypeProxyName proxyName,
        ref int scannedTypes)
    {
        using PEReader? peReader = module.OpenPeReader();
        if (peReader is null || !peReader.HasMetadata)
        {
            return null;
        }

        MetadataReader metadata = peReader.GetMetadataReader();
        if (proxyName.AssemblyName is not null &&
            (!metadata.IsAssembly || !string.Equals(
                metadata.GetString(metadata.GetAssemblyDefinition().Name),
                proxyName.AssemblyName,
                StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
        {
            if (++scannedTypes > MaximumTypeScanCount)
            {
                throw new InvalidOperationException(
                    $"Debugger proxy resolution exceeds the loaded-type scan limit of " +
                    $"{MaximumTypeScanCount}.");
            }

            if (string.Equals(
                GetMetadataTypeName(metadata, typeHandle),
                proxyName.MetadataName,
                StringComparison.Ordinal))
            {
                return checked((uint)MetadataTokens.GetToken(typeHandle));
            }
        }

        return null;
    }

    private static uint? TryGetProxyConstructor(
        MetadataReader metadata,
        TypeDefinition type)
    {
        List<MethodDefinitionHandle> constructors = [];
        foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
        {
            MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
            if ((method.Attributes & MethodAttributes.Static) == 0 &&
                string.Equals(
                    metadata.GetString(method.Name),
                    ".ctor",
                    StringComparison.Ordinal))
            {
                constructors.Add(methodHandle);
            }
        }

        if (constructors.Count != 1)
        {
            return null;
        }

        MethodDefinition constructor = metadata.GetMethodDefinition(constructors[0]);
        MethodSignature<string> signature = constructor.DecodeSignature(
            FunctionEvaluationSignatureTypeProvider.Instance,
            genericContext: null);
        return !signature.Header.IsGeneric && signature.ParameterTypes.Length == 1
            ? checked((uint)MetadataTokens.GetToken(constructors[0]))
            : null;
    }

    private static unsafe List<nint> EnumerateTypeArguments(nint type)
    {
        nint enumerator = 0;
        try
        {
            nint* enumeratorAddress = &enumerator;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugTypeAbi(type).EnumerateTypeParameters((nint)enumeratorAddress),
                "ICorDebugType.EnumerateTypeParameters");
            enumerator = RequirePointer(
                Volatile.Read(ref *enumeratorAddress),
                "ICorDebugType.EnumerateTypeParameters");
            List<nint> result = [];
            var values = new ICorDebugTypeEnumAbi(enumerator);
            for (int index = 0; index < MaximumRuntimeTypeArgumentCount; index++)
            {
                nint argument = 0;
                uint fetched = 0;
                nint* argumentAddress = &argument;
                uint* fetchedAddress = &fetched;
                CorDebugHResult.ThrowIfFailed(
                    values.Next(1, (nint)argumentAddress, (nint)fetchedAddress),
                    "ICorDebugTypeEnum.Next");
                argument = Volatile.Read(ref *argumentAddress);
                if (Volatile.Read(ref *fetchedAddress) == 0)
                {
                    return result;
                }

                result.Add(RequirePointer(argument, "ICorDebugTypeEnum.Next"));
            }

            ReleaseAll(result);
            throw new InvalidOperationException(
                $"The runtime type exceeds the generic argument limit of " +
                $"{MaximumRuntimeTypeArgumentCount}.");
        }
        finally
        {
            if (enumerator != 0)
            {
                _ = ComAbi.Release(enumerator);
            }
        }
    }

    private PEReader OpenRuntimeModule(nint module) => _modules
        .FindModule(module)
        ?.OpenPeReader() ?? new PEReader(new FileStream(
            CorDebugModulePath.Get(module),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete));

    private static string GetMetadataTypeName(
        MetadataReader metadata,
        TypeDefinitionHandle handle)
    {
        TypeDefinition type = metadata.GetTypeDefinition(handle);
        string name = metadata.GetString(type.Name);
        TypeDefinitionHandle declaringType = type.GetDeclaringType();
        if (!declaringType.IsNil)
        {
            return $"{GetMetadataTypeName(metadata, declaringType)}+{name}";
        }

        string typeNamespace = metadata.GetString(type.Namespace);
        return string.IsNullOrEmpty(typeNamespace) ? name : $"{typeNamespace}.{name}";
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
        return RequirePointer(Volatile.Read(ref *moduleAddress), "ICorDebugClass.GetModule");
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

    private static void ReleaseAll(IEnumerable<nint> pointers)
    {
        foreach (nint pointer in pointers.Where(static pointer => pointer != 0))
        {
            _ = ComAbi.Release(pointer);
        }
    }
}
