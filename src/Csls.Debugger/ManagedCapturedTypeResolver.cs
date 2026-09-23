using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Constructs exact captured field types from scoped metadata and the declaring runtime's type arguments.
/// </summary>
internal sealed unsafe class ManagedCapturedTypeResolver
{
    private readonly Func<nint, PEReader> _openModule;
    private readonly ManagedScopedTypeResolver _scopes;

    /// <summary>
    /// Creates a captured signature resolver over identity-validated module snapshots.
    /// </summary>
    internal ManagedCapturedTypeResolver(Func<nint, PEReader> openModule)
    {
        _openModule = openModule;
        _scopes = new ManagedScopedTypeResolver(openModule, ResolveAssembly);
    }

    /// <summary>
    /// Returns an owned field type preserving the complete constructed declaring context.
    /// </summary>
    internal nint Resolve(ManagedRuntimeTypeDeclaration declaration, FieldDefinitionHandle field)
    {
        var provider = new ManagedMetadataTypeSignatureProvider(declaration.Module);
        ManagedMetadataTypeSignature signature = declaration.Metadata.GetFieldDefinition(field).DecodeSignature(provider, null);
        nint[] arguments = ManagedRuntimeTypeArguments.Retain(declaration.Type);
        nint coreModule = 0;
        try
        {
            coreModule = GetCoreModule(declaration.Type);
            return Resolve(signature, arguments, coreModule, declaration.Module, 0);
        }
        finally
        {
            Release(coreModule);
            foreach (nint argument in arguments)
            {
                Release(argument);
            }
        }
    }

    private nint Resolve(ManagedMetadataTypeSignature signature, IReadOnlyList<nint> arguments,
        nint coreModule, nint contextModule, int depth)
    {
        if (depth >= 256 || signature.TypeArguments.Count > 64 || signature.ArrayShapes.Count > 32)
        {
            throw new BadImageFormatException("The captured field signature exceeds the runtime type complexity limits.");
        }
        if (signature.UnsupportedKind is not null || signature.GenericMethodParameterIndex is not null)
        {
            throw new BadImageFormatException("The captured field signature does not describe an ordinary instance field.");
        }
        nint type = 0;
        try
        {
            if (signature.GenericTypeParameterIndex is { } parameter)
            {
                if ((uint)parameter >= (uint)arguments.Count)
                {
                    throw new BadImageFormatException("The captured field type parameter is outside the declaring arity.");
                }
                type = arguments[parameter];
                _ = ComAbi.AddRef(type);
            }
            else
            {
                if (signature.PrimitiveType is not null)
                {
                    using PEReader reader = _openModule(coreModule);
                    int scanned = 0;
                    uint token = ManagedMetadataTypeLookup.FindDefinition(reader.GetMetadataReader(),
                        signature.MetadataName ?? throw new BadImageFormatException("An intrinsic field type has no name."),
                        null, ref scanned) ?? throw new InvalidOperationException("The captured intrinsic type is unavailable.");
                    signature = signature with { SourceModule = coreModule, DefinitionToken = token, AssemblyReferenceToken = 0 };
                }
                if (!_scopes.TryResolve(signature, out nint module, out uint definition))
                {
                    throw new InvalidOperationException($"The captured field type '{signature.MetadataName}' could not be resolved in its assembly scope.");
                }
                nint[] nested = new nint[signature.TypeArguments.Count];
                try
                {
                    for (int index = 0; index < nested.Length; index++)
                    {
                        nested[index] = Resolve(signature.TypeArguments[index], arguments, coreModule, contextModule, depth + 1);
                    }
                    type = ManagedRuntimeTypeConstruction.Create(module, definition, signature.IsValueType, nested);
                }
                finally
                {
                    Release(module);
                    foreach (nint argument in nested)
                    {
                        Release(argument);
                    }
                }
            }
            for (int index = 0; index < signature.ArrayShapes.Count; index++)
            {
                nint array = CreateArray(type, signature.ArrayShapes[index], contextModule);
                Release(type);
                type = array;
            }
            nint result = type;
            type = 0;
            return result;
        }
        finally
        {
            Release(type);
        }
    }

    private static nint GetCoreModule(nint type)
    {
        _ = ComAbi.AddRef(type);
        nint current = type;
        nint runtimeClass = 0;
        nint module = 0;
        try
        {
            for (int depth = 0; depth < 256; depth++)
            {
                nint parent = 0;
                int result = new ICorDebugTypeAbi(current).GetBase((nint)(&parent));
                parent = Volatile.Read(ref parent);
                if (result < 0)
                {
                    Release(parent);
                    CorDebugHResult.ThrowIfFailed(result, "ICorDebugType.GetBase");
                }
                if (parent == 0)
                {
                    CorDebugHResult.ThrowIfFailed(new ICorDebugTypeAbi(current).GetClass((nint)(&runtimeClass)), "ICorDebugType.GetClass");
                    runtimeClass = Volatile.Read(ref runtimeClass);
                    CorDebugHResult.ThrowIfFailed(new ICorDebugClassAbi(runtimeClass).GetModule((nint)(&module)), "ICorDebugClass.GetModule");
                    module = Volatile.Read(ref module);
                    nint owned = module;
                    module = 0;
                    return owned;
                }
                Release(current);
                current = parent;
            }
            throw new InvalidDataException("The captured runtime hierarchy exceeds 256 declarations.");
        }
        finally
        {
            Release(module);
            Release(runtimeClass);
            Release(current);
        }
    }

    private static nint CreateArray(nint element, ManagedMetadataArrayShape shape, nint module)
    {
        nint assembly = 0;
        nint domain = 0;
        nint domain2 = 0;
        nint array = 0;
        try
        {
            CorDebugHResult.ThrowIfFailed(new ICorDebugModuleAbi(module).GetAssembly((nint)(&assembly)), "ICorDebugModule.GetAssembly");
            assembly = Volatile.Read(ref assembly);
            CorDebugHResult.ThrowIfFailed(new ICorDebugAssemblyAbi(assembly).GetAppDomain((nint)(&domain)), "ICorDebugAssembly.GetAppDomain");
            domain2 = ComAbi.QueryInterface(Volatile.Read(ref domain), ICorDebugAppDomain2Abi.InterfaceId);
            CorDebugHResult.ThrowIfFailed(new ICorDebugAppDomain2Abi(domain2).GetArrayOrPointerType(
                shape.IsVector ? 0x1du : 0x14u, checked((uint)shape.Rank), element, (nint)(&array)),
                "ICorDebugAppDomain2.GetArrayOrPointerType");
            array = Volatile.Read(ref array);
            nint result = array;
            array = 0;
            return result;
        }
        finally
        {
            Release(array);
            Release(domain2);
            Release(domain);
            Release(assembly);
        }
    }

    private static nint ResolveAssembly(nint module, uint token)
    {
        if (token == 0)
        {
            nint owner = 0;
            int result = new ICorDebugModuleAbi(module).GetAssembly((nint)(&owner));
            owner = Volatile.Read(ref owner);
            if (result < 0)
            {
                Release(owner);
                CorDebugHResult.ThrowIfFailed(result, "ICorDebugModule.GetAssembly");
            }
            return owner;
        }
        nint module2 = ComAbi.QueryInterface(module, ICorDebugModule2Abi.InterfaceId);
        nint assembly = 0;
        try
        {
            int result = new ICorDebugModule2Abi(module2).ResolveAssembly(token, (nint)(&assembly));
            assembly = Volatile.Read(ref assembly);
            if (result < 0)
            {
                Release(assembly);
                CorDebugHResult.ThrowIfFailed(result, "ICorDebugModule2.ResolveAssembly");
            }
            return assembly;
        }
        finally
        {
            Release(module2);
        }
    }

    private static void Release(nint value)
    {
        if (value != 0)
        {
            _ = ComAbi.Release(value);
        }
    }
}
