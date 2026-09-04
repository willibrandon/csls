using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves Results View proxies from exact runtime interface implementations.
/// </summary>
internal sealed class ManagedResultsViewResolver
{
    private const uint ArrayElementType = 0x14;
    private const uint ClassElementType = 0x12;
    private const string EnumerableMetadataName = "System.Collections.IEnumerable";
    private const string GenericEnumerableMetadataName =
        "System.Collections.Generic.IEnumerable`1";
    private const int MaximumHierarchyDepth = 256;
    private const int MaximumInterfaceCount = 4096;
    private const string NonGenericDebugViewMetadataName =
        "System.Linq.SystemCore_EnumerableDebugView";
    private const string GenericDebugViewMetadataName =
        "System.Linq.SystemCore_EnumerableDebugView`1";
    private const uint SingleDimensionArrayElementType = 0x1D;
    private const uint ValueTypeElementType = 0x11;
    private readonly SourceBreakpointManager _modules;
    private readonly ManagedRuntimeTypeCatalog _typeCatalog;

    /// <summary>
    /// Creates a Results View resolver over the loaded runtime-module catalog.
    /// </summary>
    /// <param name="modules">The current loaded runtime-module catalog.</param>
    internal ManagedResultsViewResolver(SourceBreakpointManager modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _modules = modules;
        _typeCatalog = new ManagedRuntimeTypeCatalog(modules);
    }

    /// <summary>
    /// Gets whether one runtime value has a fully resolvable Results View proxy.
    /// </summary>
    /// <param name="value">The retained, dereferenced runtime value.</param>
    /// <param name="thread">The retained runtime thread used to construct exact types.</param>
    /// <returns>True when Results View can be constructed and enumerated.</returns>
    internal bool CanResolve(nint value, nint thread)
    {
        if (!TryResolve(value, thread, out ManagedResultsViewBinding? binding) ||
            binding is null)
        {
            return false;
        }

        binding.Release();
        return true;
    }

    /// <summary>
    /// Identifies the runtime's dedicated empty-enumeration sentinel exception.
    /// </summary>
    /// <param name="value">The retained, dereferenced exception value.</param>
    /// <param name="itemsGetter">The retained Items getter that produced the exception.</param>
    /// <returns>True for the sentinel defined by the exact Items getter module.</returns>
    internal unsafe bool IsEmptyEnumerationException(nint value, nint itemsGetter)
    {
        nint value2 = ComAbi.QueryInterface(value, ICorDebugValue2Abi.InterfaceId);
        nint exactType = 0;
        nint getterModule = 0;
        nint getterModuleIdentity = 0;
        nint exceptionModuleIdentity = 0;
        try
        {
            nint* exactTypeAddress = &exactType;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugValue2Abi(value2).GetExactType((nint)exactTypeAddress),
                "ICorDebugValue2.GetExactType");
            exactType = RequirePointer(Volatile.Read(ref *exactTypeAddress), "ICorDebugValue2.GetExactType");
            if (!IsNamedRuntimeType(exactType, "System.Linq.SystemCore_EnumerableDebugViewEmptyException"))
            {
                return false;
            }

            nint runtimeClass = GetRuntimeTypeClass(exactType);
            try
            {
                nint module = GetClassModule(runtimeClass);
                try
                {
                    nint* getterModuleAddress = &getterModule;
                    CorDebugHResult.ThrowIfFailed(
                        new ICorDebugFunctionAbi(itemsGetter).GetModule((nint)getterModuleAddress),
                        "ICorDebugFunction.GetModule");
                    getterModule = RequirePointer(
                        Volatile.Read(ref *getterModuleAddress), "ICorDebugFunction.GetModule");
                    getterModuleIdentity = ComAbi.GetIdentity(getterModule);
                    exceptionModuleIdentity = ComAbi.GetIdentity(module);
                    return getterModuleIdentity == exceptionModuleIdentity;
                }
                finally
                {
                    ReleasePointer(module);
                }
            }
            finally
            {
                ReleasePointer(runtimeClass);
            }
        }
        finally
        {
            ReleasePointer(exceptionModuleIdentity);
            ReleasePointer(getterModuleIdentity);
            ReleasePointer(getterModule);
            ReleasePointer(exactType);
            ReleasePointer(value2);
        }
    }

    /// <summary>
    /// Tries to resolve the exact debug-view constructor and Items getter.
    /// </summary>
    /// <param name="value">The retained, dereferenced runtime value.</param>
    /// <param name="thread">The retained runtime thread used to construct exact types.</param>
    /// <param name="binding">Receives owned runtime functions and type arguments.</param>
    /// <returns>True when Results View can be constructed and enumerated.</returns>
    internal bool TryResolve(
        nint value,
        nint thread,
        out ManagedResultsViewBinding? binding)
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
            binding?.Release();
            binding = null;
            return false;
        }
    }

    private unsafe bool TryResolveCore(
        nint value,
        nint thread,
        out ManagedResultsViewBinding? binding)
    {
        binding = null;
        if (ComAbi.TryQueryInterface(value, ICorDebugArrayValueAbi.InterfaceId, out nint array))
        {
            _ = ComAbi.Release(array);
            return false;
        }

        nint value2 = 0;
        nint exactType = 0;
        nint elementType = 0;
        nint constructor = 0;
        nint itemsGetter = 0;
        nint[] typeArguments = [];
        try
        {
            value2 = ComAbi.QueryInterface(value, ICorDebugValue2Abi.InterfaceId);
            nint* exactTypeAddress = &exactType;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugValue2Abi(value2).GetExactType((nint)exactTypeAddress),
                "ICorDebugValue2.GetExactType");
            exactType = RequirePointer(
                Volatile.Read(ref *exactTypeAddress),
                "ICorDebugValue2.GetExactType");
            if (IsNamedRuntimeType(exactType, "System.String"))
            {
                return false;
            }

            bool isGeneric = TryFindGenericEnumerable(
                exactType,
                thread,
                out elementType);
            if (!isGeneric && !ImplementsNonGenericEnumerable(exactType, thread))
            {
                return false;
            }

            string debugViewName = isGeneric
                ? GenericDebugViewMetadataName
                : NonGenericDebugViewMetadataName;
            if (!_typeCatalog.TryFindLoadedType(
                debugViewName,
                "System.Linq",
                out CorDebugLoadedModule? module,
                out uint typeToken) ||
                module is null ||
                !ManagedResultsViewMetadata.TryGetMembers(
                    module,
                    typeToken,
                    isGeneric,
                    out uint constructorToken,
                    out uint itemsGetterToken))
            {
                return false;
            }

            constructor = GetModuleFunction(module.Pointer, constructorToken);
            itemsGetter = GetModuleFunction(module.Pointer, itemsGetterToken);
            if (isGeneric)
            {
                typeArguments = [elementType];
                elementType = 0;
            }

            binding = new ManagedResultsViewBinding(
                constructor,
                itemsGetter,
                typeArguments);
            constructor = 0;
            itemsGetter = 0;
            typeArguments = [];
            return true;
        }
        finally
        {
            ReleaseAll(typeArguments);
            ReleasePointer(itemsGetter);
            ReleasePointer(constructor);
            ReleasePointer(elementType);
            ReleasePointer(exactType);
            ReleasePointer(value2);
        }
    }

    private bool TryFindGenericEnumerable(
        nint exactType,
        nint thread,
        out nint elementType)
    {
        elementType = 0;
        nint currentType = Retain(exactType);
        var visited = new HashSet<nint>();
        int interfaceCount = 0;
        try
        {
            for (int depth = 0;
                currentType != 0 && depth < MaximumHierarchyDepth;
                depth++)
            {
                nint baseType = 0;
                try
                {
                    if (TryFindGenericEnumerableOnInterface(
                        currentType,
                        thread,
                        visited,
                        ref interfaceCount,
                        out elementType))
                    {
                        return true;
                    }

                    baseType = GetBaseType(currentType);
                }
                finally
                {
                    ReleasePointer(currentType);
                    currentType = baseType;
                }
            }

            if (currentType != 0)
            {
                throw new InvalidOperationException(
                    $"The runtime type hierarchy exceeds {MaximumHierarchyDepth} levels.");
            }

            return false;
        }
        finally
        {
            ReleasePointer(currentType);
        }
    }

    private bool ImplementsNonGenericEnumerable(nint exactType, nint thread)
    {
        nint currentType = Retain(exactType);
        var visited = new HashSet<nint>();
        int interfaceCount = 0;
        try
        {
            for (int depth = 0;
                currentType != 0 && depth < MaximumHierarchyDepth;
                depth++)
            {
                nint baseType = 0;
                try
                {
                    if (ImplementsNonGenericEnumerableOnInterface(
                        currentType,
                        thread,
                        visited,
                        ref interfaceCount))
                    {
                        return true;
                    }

                    baseType = GetBaseType(currentType);
                }
                finally
                {
                    ReleasePointer(currentType);
                    currentType = baseType;
                }
            }

            if (currentType != 0)
            {
                throw new InvalidOperationException(
                    $"The runtime type hierarchy exceeds {MaximumHierarchyDepth} levels.");
            }

            return false;
        }
        finally
        {
            ReleasePointer(currentType);
        }
    }

    private bool TryFindGenericEnumerableOnInterface(
        nint runtimeType,
        nint thread,
        HashSet<nint> visited,
        ref int interfaceCount,
        out nint elementType,
        int depth = 0)
    {
        elementType = 0;
        EnsureHierarchyDepth(depth);
        if (!TryVisit(runtimeType, visited))
        {
            return false;
        }

        List<ManagedMetadataTypeSignature> interfaces = ReadInterfaces(runtimeType);
        nint[] genericArguments = ManagedRuntimeTypeArguments.Retain(runtimeType);
        try
        {
            foreach (ManagedMetadataTypeSignature candidate in interfaces)
            {
                EnsureInterfaceLimit(ref interfaceCount);
                if (string.Equals(
                    candidate.MetadataName,
                    GenericEnumerableMetadataName,
                    StringComparison.Ordinal) &&
                    candidate.TypeArguments is [ManagedMetadataTypeSignature argument])
                {
                    elementType = ResolveRuntimeType(argument, genericArguments, thread);
                    return true;
                }
            }

            foreach (ManagedMetadataTypeSignature candidate in interfaces)
            {
                nint interfaceType = 0;
                try
                {
                    if (TryResolveInterface(candidate, genericArguments, thread, out interfaceType) &&
                        TryFindGenericEnumerableOnInterface(
                        interfaceType,
                        thread,
                        visited,
                        ref interfaceCount,
                        out elementType,
                        depth + 1))
                    {
                        return true;
                    }
                }
                finally
                {
                    ReleasePointer(interfaceType);
                }
            }

            return false;
        }
        finally
        {
            ReleaseAll(genericArguments);
        }
    }

    private bool ImplementsNonGenericEnumerableOnInterface(
        nint runtimeType,
        nint thread,
        HashSet<nint> visited,
        ref int interfaceCount,
        int depth = 0)
    {
        EnsureHierarchyDepth(depth);
        if (!TryVisit(runtimeType, visited))
        {
            return false;
        }

        List<ManagedMetadataTypeSignature> interfaces = ReadInterfaces(runtimeType);
        nint[] genericArguments = ManagedRuntimeTypeArguments.Retain(runtimeType);
        try
        {
            foreach (ManagedMetadataTypeSignature candidate in interfaces)
            {
                EnsureInterfaceLimit(ref interfaceCount);
                if (string.Equals(
                    candidate.MetadataName,
                    EnumerableMetadataName,
                    StringComparison.Ordinal))
                {
                    return true;
                }
            }

            foreach (ManagedMetadataTypeSignature candidate in interfaces)
            {
                nint interfaceType = 0;
                try
                {
                    if (TryResolveInterface(candidate, genericArguments, thread, out interfaceType) &&
                        ImplementsNonGenericEnumerableOnInterface(
                        interfaceType,
                        thread,
                        visited,
                        ref interfaceCount,
                        depth + 1))
                    {
                        return true;
                    }
                }
                finally
                {
                    ReleasePointer(interfaceType);
                }
            }

            return false;
        }
        finally
        {
            ReleaseAll(genericArguments);
        }
    }

    private bool TryResolveInterface(
        ManagedMetadataTypeSignature signature,
        IReadOnlyList<nint> genericArguments,
        nint thread,
        out nint runtimeType)
    {
        runtimeType = 0;
        try
        {
            runtimeType = ResolveRuntimeType(signature, genericArguments, thread);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or BadImageFormatException or OverflowException)
        {
            return false;
        }
    }

    private unsafe nint ResolveRuntimeType(
        ManagedMetadataTypeSignature signature,
        IReadOnlyList<nint> genericArguments,
        nint thread,
        int depth = 0)
    {
        EnsureHierarchyDepth(depth);
        nint result = 0;
        if (signature.GenericTypeParameterIndex is int parameterIndex)
        {
            if ((uint)parameterIndex >= (uint)genericArguments.Count)
            {
                throw new BadImageFormatException(
                    $"Generic parameter {parameterIndex} is outside the runtime type arity.");
            }

            result = Retain(genericArguments[parameterIndex]);
        }
        else
        {
            string metadataName = signature.MetadataName ??
                throw new BadImageFormatException("A metadata type signature has no identity.");
            if (!_typeCatalog.TryResolveSignature(
                signature,
                out CorDebugLoadedModule? module,
                out uint typeToken) ||
                module is null)
            {
                throw new InvalidOperationException(
                    $"Runtime type '{metadataName}' is not loaded uniquely.");
            }

            nint runtimeClass = 0;
            nint runtimeClass2 = 0;
            nint[] typeArguments = new nint[signature.TypeArguments.Count];
            try
            {
                for (int index = 0; index < typeArguments.Length; index++)
                {
                    typeArguments[index] = ResolveRuntimeType(
                        signature.TypeArguments[index],
                        genericArguments,
                        thread,
                        depth + 1);
                }

                runtimeClass = GetModuleClass(module.Pointer, typeToken);
                runtimeClass2 = ComAbi.QueryInterface(
                    runtimeClass,
                    ICorDebugClass2Abi.InterfaceId);
                fixed (nint* typeArgumentsAddress = typeArguments)
                {
                    nint* resultAddress = &result;
                    CorDebugHResult.ThrowIfFailed(
                        new ICorDebugClass2Abi(runtimeClass2).GetParameterizedType(
                            signature.IsValueType ? ValueTypeElementType : ClassElementType,
                            checked((uint)typeArguments.Length),
                            typeArguments.Length == 0 ? 0 : (nint)typeArgumentsAddress,
                            (nint)resultAddress),
                        "ICorDebugClass2.GetParameterizedType");
                    result = RequirePointer(
                        Volatile.Read(ref *resultAddress),
                        "ICorDebugClass2.GetParameterizedType");
                }
            }
            finally
            {
                ReleaseAll(typeArguments);
                ReleasePointer(runtimeClass2);
                ReleasePointer(runtimeClass);
            }
        }

        if (signature.ArrayShapes.Count == 0)
        {
            return result;
        }

        return ApplyArrayShapes(result, signature.ArrayShapes, thread);
    }

    private static unsafe nint ApplyArrayShapes(
        nint elementType,
        IReadOnlyList<ManagedMetadataArrayShape> shapes,
        nint thread)
    {
        nint appDomain = 0;
        nint appDomain2 = 0;
        nint currentType = elementType;
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
            foreach (ManagedMetadataArrayShape shape in shapes)
            {
                nint arrayType = 0;
                nint* arrayTypeAddress = &arrayType;
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugAppDomain2Abi(appDomain2).GetArrayOrPointerType(
                        shape.IsVector
                            ? SingleDimensionArrayElementType
                            : ArrayElementType,
                        checked((uint)shape.Rank),
                        currentType,
                        (nint)arrayTypeAddress),
                    "ICorDebugAppDomain2.GetArrayOrPointerType");
                arrayType = RequirePointer(
                    Volatile.Read(ref *arrayTypeAddress),
                    "ICorDebugAppDomain2.GetArrayOrPointerType");
                ReleasePointer(currentType);
                currentType = arrayType;
            }

            return currentType;
        }
        catch
        {
            ReleasePointer(currentType);
            throw;
        }
        finally
        {
            ReleasePointer(appDomain2);
            ReleasePointer(appDomain);
        }
    }

    private List<ManagedMetadataTypeSignature> ReadInterfaces(nint runtimeType)
    {
        nint runtimeClass = 0;
        nint module = 0;
        try
        {
            runtimeClass = GetRuntimeTypeClass(runtimeType);
            module = GetClassModule(runtimeClass);
            uint typeToken = GetClassToken(runtimeClass);
            using PEReader peReader = OpenRuntimeModule(module);
            MetadataReader metadata = peReader.GetMetadataReader();
            TypeDefinition type = metadata.GetTypeDefinition(
                MetadataTokens.TypeDefinitionHandle(checked((int)(typeToken & 0x00FFFFFF))));
            var result = new List<ManagedMetadataTypeSignature>();
            var provider = new ManagedMetadataTypeSignatureProvider(module);
            foreach (InterfaceImplementation implementation in type
                .GetInterfaceImplementations()
                .Select(metadata.GetInterfaceImplementation))
            {
                result.Add(DecodeType(provider, metadata, implementation.Interface));
            }

            return result;
        }
        finally
        {
            ReleasePointer(module);
            ReleasePointer(runtimeClass);
        }
    }

    private static ManagedMetadataTypeSignature DecodeType(
        ManagedMetadataTypeSignatureProvider provider,
        MetadataReader metadata,
        EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => provider
                .GetTypeFromDefinition(
                    metadata,
                    (TypeDefinitionHandle)handle,
                    checked((byte)ClassElementType)),
            HandleKind.TypeReference => provider
                .GetTypeFromReference(
                    metadata,
                    (TypeReferenceHandle)handle,
                    checked((byte)ClassElementType)),
            HandleKind.TypeSpecification => provider
                .GetTypeFromSpecification(
                    metadata,
                    null,
                    (TypeSpecificationHandle)handle,
                    checked((byte)ClassElementType)),
            _ => throw new BadImageFormatException(
                $"Unsupported interface metadata handle {handle.Kind}.")
        };

    private bool IsNamedRuntimeType(nint runtimeType, string metadataName)
    {
        nint runtimeClass = 0;
        nint module = 0;
        try
        {
            runtimeClass = GetRuntimeTypeClass(runtimeType);
            module = GetClassModule(runtimeClass);
            uint token = GetClassToken(runtimeClass);
            using PEReader peReader = OpenRuntimeModule(module);
            MetadataReader metadata = peReader.GetMetadataReader();
            TypeDefinitionHandle handle = MetadataTokens.TypeDefinitionHandle(
                checked((int)(token & 0x00FFFFFF)));
            return string.Equals(
                GetMetadataTypeName(metadata, handle),
                metadataName,
                StringComparison.Ordinal);
        }
        finally
        {
            ReleasePointer(module);
            ReleasePointer(runtimeClass);
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

    private static unsafe nint GetBaseType(nint runtimeType)
    {
        nint baseType = 0;
        nint* baseTypeAddress = &baseType;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugTypeAbi(runtimeType).GetBase((nint)baseTypeAddress),
            "ICorDebugType.GetBase");
        return Volatile.Read(ref *baseTypeAddress);
    }

    private static bool TryVisit(nint runtimeType, HashSet<nint> visited)
    {
        nint identity = ComAbi.GetIdentity(runtimeType);
        try
        {
            return visited.Add(identity);
        }
        finally
        {
            _ = ComAbi.Release(identity);
        }
    }

    private static void EnsureInterfaceLimit(ref int interfaceCount)
    {
        interfaceCount = checked(interfaceCount + 1);
        if (interfaceCount > MaximumInterfaceCount)
        {
            throw new InvalidOperationException(
                $"A runtime type exceeds {MaximumInterfaceCount} transitive interfaces.");
        }
    }

    private static void EnsureHierarchyDepth(int depth)
    {
        if (depth >= MaximumHierarchyDepth)
        {
            throw new BadImageFormatException(
                $"A runtime interface or signature exceeds {MaximumHierarchyDepth} nested levels.");
        }
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

    private static nint Retain(nint pointer)
    {
        _ = ComAbi.AddRef(pointer);
        return pointer;
    }

    private static nint RequirePointer(nint pointer, string operation) => pointer != 0
        ? pointer
        : throw new InvalidOperationException($"{operation} returned a null pointer.");

    private static void ReleaseAll(IEnumerable<nint> pointers)
    {
        foreach (nint pointer in pointers)
        {
            ReleasePointer(pointer);
        }
    }

    private static void ReleasePointer(nint pointer)
    {
        if (pointer != 0)
        {
            _ = ComAbi.Release(pointer);
        }
    }
}
