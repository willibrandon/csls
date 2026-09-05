using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Resolves scoped metadata signatures to owned exact runtime types without executing target code.
/// </summary>
internal sealed class ManagedRuntimeTypeResolver
{
    private const uint ArrayElementType = 0x14;
    private const uint ClassElementType = 0x12;
    private const uint SingleDimensionArrayElementType = 0x1D;
    private const uint ValueTypeElementType = 0x11;
    private const int MaximumHierarchyDepth = 256;
    private readonly ManagedRuntimeTypeCatalog _typeCatalog;
    private readonly ManagedCoreLibrary _coreLibrary;

    /// <summary>
    /// Creates a signature resolver using the session's exact loaded assembly bindings.
    /// </summary>
    /// <param name="typeCatalog">The loaded runtime type catalog.</param>
    /// <param name="coreLibrary">The resolver for intrinsic runtime types.</param>
    internal ManagedRuntimeTypeResolver(ManagedRuntimeTypeCatalog typeCatalog, ManagedCoreLibrary coreLibrary)
    {
        ArgumentNullException.ThrowIfNull(typeCatalog);
        ArgumentNullException.ThrowIfNull(coreLibrary);
        _typeCatalog = typeCatalog;
        _coreLibrary = coreLibrary;
    }

    /// <summary>
    /// Resolves one metadata signature and transfers its native type reference to the caller.
    /// </summary>
    /// <param name="signature">The decoded signature preserving its defining module.</param>
    /// <param name="genericArguments">The borrowed exact arguments of its declaring type.</param>
    /// <param name="methodArguments">The borrowed exact arguments of its declaring method.</param>
    /// <param name="thread">The borrowed thread identifying the target application domain.</param>
    /// <returns>An owned ICorDebugType reference that the caller must release.</returns>
    internal nint Resolve(
        ManagedMetadataTypeSignature signature,
        IReadOnlyList<nint> genericArguments,
        IReadOnlyList<nint> methodArguments,
        nint thread) => ResolveCore(signature, genericArguments, methodArguments, thread, depth: 0);

    private unsafe nint ResolveCore(
        ManagedMetadataTypeSignature signature,
        IReadOnlyList<nint> genericArguments,
        IReadOnlyList<nint> methodArguments,
        nint thread,
        int depth)
    {
        EnsureHierarchyDepth(depth);
        if (signature.UnsupportedKind is string unsupportedKind)
        {
            throw new BadImageFormatException(
                $"The debugger cannot resolve a {unsupportedKind} metadata signature as an ordinary value type.");
        }

        nint result = 0;
        if (signature.GenericTypeParameterIndex is not null && signature.GenericMethodParameterIndex is not null)
        {
            throw new BadImageFormatException("A metadata signature cannot identify both a type and method parameter.");
        }

        int? genericParameterIndex = signature.GenericTypeParameterIndex ?? signature.GenericMethodParameterIndex;
        if (genericParameterIndex is int parameterIndex)
        {
            IReadOnlyList<nint> arguments = signature.GenericMethodParameterIndex is null
                ? genericArguments : methodArguments;
            if ((uint)parameterIndex >= (uint)arguments.Count)
            {
                throw new BadImageFormatException(
                    $"Generic parameter {parameterIndex} is outside the runtime type arity.");
            }

            result = Retain(arguments[parameterIndex]);
        }
        else
        {
            if (signature.PrimitiveType is not null)
            {
                signature = _coreLibrary.Resolve(signature, thread);
            }

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
                    typeArguments[index] = ResolveCore(
                        signature.TypeArguments[index],
                        genericArguments,
                        methodArguments,
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
            catch
            {
                ReleasePointer(result);
                throw;
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
                try
                {
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
                    arrayType = 0;
                }
                finally
                {
                    ReleasePointer(arrayType);
                }
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

    private static void EnsureHierarchyDepth(int depth)
    {
        if (depth >= MaximumHierarchyDepth)
        {
            throw new BadImageFormatException(
                $"A runtime type signature exceeds {MaximumHierarchyDepth} nested levels.");
        }
    }

    private static unsafe nint GetModuleClass(nint module, uint typeToken)
    {
        nint runtimeClass = 0;
        nint* runtimeClassAddress = &runtimeClass;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugModuleAbi(module).GetClassFromToken(
                typeToken, (nint)runtimeClassAddress),
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
