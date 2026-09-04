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
    private const uint ArrayElementType = 0x14;
    private const uint ClassElementType = 0x12;
    private const int MaximumHierarchyDepth = 256;
    private const int MaximumRuntimeTypeArgumentCount = 64;
    private const int MaximumTypeScanCount = 1_000_000;
    private const uint SingleDimensionArrayElementType = 0x1D;
    private const uint ValueTypeElementType = 0x11;
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
    /// <param name="thread">The retained runtime thread used to construct array types.</param>
    /// <param name="binding">Receives owned constructor and type pointers.</param>
    /// <returns>True when a valid loaded proxy can be constructed.</returns>
    internal bool TryResolve(
        nint value,
        nint thread,
        out ManagedDebuggerTypeProxyBinding? binding)
    {
        binding = null;
        try
        {
            return TryResolveCore(value, thread, out binding);
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
        nint thread,
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
                        thread,
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
        nint thread,
        out ManagedDebuggerTypeProxyBinding? binding)
    {
        binding = null;
        if (!ManagedDebuggerTypeProxyNameParser.TryParse(
            attribute.ProxyTypeName,
            out ManagedDebuggerTypeProxyName? proxyName) ||
            proxyName is null ||
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

        int proxyArity = type.GetGenericParameters().Count;
        int suppliedArity = proxyName.IsConstructed
            ? proxyName.ParsedName.GetGenericArguments().Length
            : EnumerateTypeArgumentCount(attributedRuntimeType);
        if (proxyArity != suppliedArity)
        {
            return false;
        }

        nint function = 0;
        nint[] proxyTypeArguments = [];
        try
        {
            proxyTypeArguments = proxyName.IsConstructed
                ? ResolveTypeArguments(proxyName.ParsedName, thread)
                : [.. EnumerateTypeArguments(attributedRuntimeType)];
            function = GetModuleFunction(module.Pointer, constructorToken.Value);
            binding = new ManagedDebuggerTypeProxyBinding(function, proxyTypeArguments);
            function = 0;
            proxyTypeArguments = [];
            return true;
        }
        finally
        {
            ReleaseAll(proxyTypeArguments);
            if (function != 0)
            {
                _ = ComAbi.Release(function);
            }
        }
    }

    private bool TryFindLoadedType(
        ManagedDebuggerTypeProxyName proxyName,
        out CorDebugLoadedModule? resolvedModule,
        out uint resolvedToken) => TryFindLoadedType(
            proxyName.MetadataName,
            proxyName.AssemblyName,
            out resolvedModule,
            out resolvedToken);

    private bool TryFindLoadedType(
        string metadataName,
        string? assemblyName,
        out CorDebugLoadedModule? resolvedModule,
        out uint resolvedToken)
    {
        resolvedModule = null;
        resolvedToken = 0;
        int scannedTypes = 0;
        var visitedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int depth = 0; depth < MaximumHierarchyDepth; depth++)
        {
            foreach (CorDebugLoadedModule module in _modules.GetRuntimeModules())
            {
                uint? match = TryFindTypeInModule(
                    module,
                    metadataName,
                    assemblyName,
                    ref scannedTypes);
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

            if (resolvedModule is not null)
            {
                return true;
            }

            if (assemblyName is null ||
                !visitedAssemblies.Add(assemblyName) ||
                !TryResolveForwardedAssembly(
                    metadataName,
                    assemblyName,
                    out string? forwardedAssembly))
            {
                return false;
            }

            assemblyName = forwardedAssembly;
        }

        return false;
    }

    private bool TryResolveForwardedAssembly(
        string metadataName,
        string assemblyName,
        out string? forwardedAssembly)
    {
        forwardedAssembly = null;
        foreach (string candidate in _modules.GetRuntimeModules()
            .Select(module => GetForwardedAssembly(module, metadataName, assemblyName))
            .OfType<string>())
        {
            if (forwardedAssembly is not null && !string.Equals(
                forwardedAssembly,
                candidate,
                StringComparison.OrdinalIgnoreCase))
            {
                forwardedAssembly = null;
                return false;
            }

            forwardedAssembly = candidate;
        }

        return forwardedAssembly is not null;
    }

    private static string? GetForwardedAssembly(
        CorDebugLoadedModule module,
        string metadataName,
        string assemblyName)
    {
        using PEReader? peReader = module.OpenPeReader();
        if (peReader is null || !peReader.HasMetadata)
        {
            return null;
        }

        MetadataReader metadata = peReader.GetMetadataReader();
        if (!metadata.IsAssembly || !string.Equals(
            metadata.GetString(metadata.GetAssemblyDefinition().Name),
            assemblyName,
            StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? forwardedAssembly = null;
        foreach (ExportedTypeHandle handle in metadata.ExportedTypes)
        {
            if (!string.Equals(
                GetExportedTypeName(metadata, handle),
                metadataName,
                StringComparison.Ordinal) ||
                !TryGetForwardedAssembly(metadata, handle, out string? candidate))
            {
                continue;
            }

            if (forwardedAssembly is not null && !string.Equals(
                forwardedAssembly,
                candidate,
                StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            forwardedAssembly = candidate;
        }

        return forwardedAssembly;
    }

    private static string GetExportedTypeName(
        MetadataReader metadata,
        ExportedTypeHandle handle)
    {
        List<string> names = [];
        for (int depth = 0; depth < MaximumHierarchyDepth; depth++)
        {
            ExportedType type = metadata.GetExportedType(handle);
            names.Add(metadata.GetString(type.Name));
            if (type.Implementation.Kind != HandleKind.ExportedType)
            {
                names.Reverse();
                string name = string.Join('+', names);
                string typeNamespace = metadata.GetString(type.Namespace);
                return string.IsNullOrEmpty(typeNamespace)
                    ? name
                    : $"{typeNamespace}.{name}";
            }

            handle = (ExportedTypeHandle)type.Implementation;
        }

        throw new BadImageFormatException(
            $"An exported type exceeds the nesting limit of {MaximumHierarchyDepth}.");
    }

    private static bool TryGetForwardedAssembly(
        MetadataReader metadata,
        ExportedTypeHandle handle,
        out string? assemblyName)
    {
        assemblyName = null;
        for (int depth = 0; depth < MaximumHierarchyDepth; depth++)
        {
            ExportedType type = metadata.GetExportedType(handle);
            if (type.Implementation.Kind == HandleKind.ExportedType)
            {
                handle = (ExportedTypeHandle)type.Implementation;
                continue;
            }

            if (!type.IsForwarder ||
                type.Implementation.Kind != HandleKind.AssemblyReference)
            {
                return false;
            }

            AssemblyReference reference = metadata.GetAssemblyReference(
                (AssemblyReferenceHandle)type.Implementation);
            assemblyName = metadata.GetString(reference.Name);
            return !string.IsNullOrEmpty(assemblyName);
        }

        throw new BadImageFormatException(
            $"An exported type exceeds the nesting limit of {MaximumHierarchyDepth}.");
    }

    private static uint? TryFindTypeInModule(
        CorDebugLoadedModule module,
        string metadataName,
        string? assemblyName,
        ref int scannedTypes)
    {
        using PEReader? peReader = module.OpenPeReader();
        if (peReader is null || !peReader.HasMetadata)
        {
            return null;
        }

        MetadataReader metadata = peReader.GetMetadataReader();
        if (assemblyName is not null &&
            (!metadata.IsAssembly || !string.Equals(
                metadata.GetString(metadata.GetAssemblyDefinition().Name),
                assemblyName,
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
                metadataName,
                StringComparison.Ordinal))
            {
                return checked((uint)MetadataTokens.GetToken(typeHandle));
            }
        }

        return null;
    }

    private nint[] ResolveTypeArguments(TypeName proxyName, nint thread)
    {
        nint[] result = new nint[proxyName.GetGenericArguments().Length];
        try
        {
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = ResolveRuntimeType(
                    proxyName.GetGenericArguments()[index],
                    thread);
            }

            return result;
        }
        catch
        {
            ReleaseAll(result);
            throw;
        }
    }

    private unsafe nint ResolveRuntimeType(TypeName typeName, nint thread)
    {
        if (typeName.IsArray)
        {
            nint elementType = ResolveRuntimeType(typeName.GetElementType(), thread);
            return ApplyArrayType(
                elementType,
                typeName.IsSZArray ? 1 : typeName.GetArrayRank(),
                typeName.IsSZArray,
                thread);
        }

        if (typeName.IsPointer || typeName.IsByRef)
        {
            throw new InvalidOperationException(
                "Debugger proxy generic arguments cannot be pointer or by-reference types.");
        }

        TypeName definitionName = typeName.IsConstructedGenericType
            ? typeName.GetGenericTypeDefinition()
            : typeName;
        string? assemblyName = typeName.AssemblyName?.Name ??
            definitionName.AssemblyName?.Name;
        if (!TryFindLoadedType(
            definitionName.FullName,
            assemblyName,
            out CorDebugLoadedModule? module,
            out uint typeToken) ||
            module is null)
        {
            throw new InvalidOperationException(
                $"Debugger proxy runtime type '{typeName.AssemblyQualifiedName}' is not " +
                "loaded uniquely.");
        }

        using PEReader? peReader = module.OpenPeReader();
        if (peReader is null || !peReader.HasMetadata)
        {
            throw new InvalidOperationException(
                $"Debugger proxy runtime type '{typeName.AssemblyQualifiedName}' has no " +
                "readable metadata.");
        }

        MetadataReader metadata = peReader.GetMetadataReader();
        TypeDefinition definition = metadata.GetTypeDefinition(
            MetadataTokens.TypeDefinitionHandle(checked((int)(typeToken & 0x00FFFFFF))));
        int expectedArity = definition.GetGenericParameters().Count;
        int suppliedArity = typeName.IsConstructedGenericType
            ? typeName.GetGenericArguments().Length
            : 0;
        if (expectedArity != suppliedArity)
        {
            throw new InvalidOperationException(
                $"Debugger proxy runtime type '{typeName.FullName}' requires " +
                $"{expectedArity} generic arguments, but {suppliedArity} were supplied.");
        }

        nint runtimeClass = 0;
        nint runtimeClass2 = 0;
        nint result = 0;
        nint[] typeArguments = typeName.IsConstructedGenericType
            ? ResolveTypeArguments(typeName, thread)
            : [];
        try
        {
            runtimeClass = GetModuleClass(module.Pointer, typeToken);
            runtimeClass2 = ComAbi.QueryInterface(
                runtimeClass,
                ICorDebugClass2Abi.InterfaceId);
            fixed (nint* typeArgumentsAddress = typeArguments)
            {
                nint* resultAddress = &result;
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugClass2Abi(runtimeClass2).GetParameterizedType(
                        IsValueTypeDefinition(metadata, definition)
                            ? ValueTypeElementType
                            : ClassElementType,
                        checked((uint)typeArguments.Length),
                        typeArguments.Length == 0 ? 0 : (nint)typeArgumentsAddress,
                        (nint)resultAddress),
                    "ICorDebugClass2.GetParameterizedType");
                result = RequirePointer(
                    Volatile.Read(ref *resultAddress),
                    "ICorDebugClass2.GetParameterizedType");
            }

            return result;
        }
        catch
        {
            if (result != 0)
            {
                _ = ComAbi.Release(result);
            }

            throw;
        }
        finally
        {
            ReleaseAll(typeArguments);
            if (runtimeClass2 != 0)
            {
                _ = ComAbi.Release(runtimeClass2);
            }

            if (runtimeClass != 0)
            {
                _ = ComAbi.Release(runtimeClass);
            }
        }
    }

    private static unsafe nint ApplyArrayType(
        nint elementType,
        int rank,
        bool singleDimension,
        nint thread)
    {
        nint appDomain = 0;
        nint appDomain2 = 0;
        nint result = 0;
        try
        {
            nint* appDomainAddress = &appDomain;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugThreadAbi(thread).GetAppDomain((nint)appDomainAddress),
                "ICorDebugThread.GetAppDomain");
            appDomain = RequirePointer(
                Volatile.Read(ref *appDomainAddress),
                "ICorDebugThread.GetAppDomain");
            appDomain2 = ComAbi.QueryInterface(
                appDomain,
                ICorDebugAppDomain2Abi.InterfaceId);
            nint* resultAddress = &result;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugAppDomain2Abi(appDomain2).GetArrayOrPointerType(
                    singleDimension ? SingleDimensionArrayElementType : ArrayElementType,
                    checked((uint)rank),
                    elementType,
                    (nint)resultAddress),
                "ICorDebugAppDomain2.GetArrayOrPointerType");
            result = RequirePointer(
                Volatile.Read(ref *resultAddress),
                "ICorDebugAppDomain2.GetArrayOrPointerType");
            return result;
        }
        catch
        {
            if (result != 0)
            {
                _ = ComAbi.Release(result);
            }

            throw;
        }
        finally
        {
            _ = ComAbi.Release(elementType);
            if (appDomain2 != 0)
            {
                _ = ComAbi.Release(appDomain2);
            }

            if (appDomain != 0)
            {
                _ = ComAbi.Release(appDomain);
            }
        }
    }

    private static bool IsValueTypeDefinition(
        MetadataReader metadata,
        TypeDefinition definition) => definition.BaseType.Kind switch
        {
            HandleKind.TypeReference => IsSystemValueTypeBase(
                metadata,
                metadata.GetTypeReference((TypeReferenceHandle)definition.BaseType)),
            HandleKind.TypeDefinition => IsSystemValueTypeBase(
                metadata,
                metadata.GetTypeDefinition((TypeDefinitionHandle)definition.BaseType)),
            _ => false
        };

    private static bool IsSystemValueTypeBase(
        MetadataReader metadata,
        TypeReference type) =>
        string.Equals(metadata.GetString(type.Namespace), "System", StringComparison.Ordinal) &&
        (string.Equals(metadata.GetString(type.Name), "ValueType", StringComparison.Ordinal) ||
            string.Equals(metadata.GetString(type.Name), "Enum", StringComparison.Ordinal));

    private static bool IsSystemValueTypeBase(
        MetadataReader metadata,
        TypeDefinition type) =>
        string.Equals(metadata.GetString(type.Namespace), "System", StringComparison.Ordinal) &&
        (string.Equals(metadata.GetString(type.Name), "ValueType", StringComparison.Ordinal) ||
            string.Equals(metadata.GetString(type.Name), "Enum", StringComparison.Ordinal));

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

    private static int EnumerateTypeArgumentCount(nint type)
    {
        List<nint> arguments = EnumerateTypeArguments(type);
        try
        {
            return arguments.Count;
        }
        finally
        {
            ReleaseAll(arguments);
        }
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

    private static unsafe nint GetModuleClass(nint module, uint typeToken)
    {
        nint runtimeClass = 0;
        nint* runtimeClassAddress = &runtimeClass;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugModuleAbi(module).GetClassFromToken(
                typeToken,
                (nint)runtimeClassAddress),
            "ICorDebugModule.GetClassFromToken");
        return RequirePointer(
            Volatile.Read(ref *runtimeClassAddress),
            "ICorDebugModule.GetClassFromToken");
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
