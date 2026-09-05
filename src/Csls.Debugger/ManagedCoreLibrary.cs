using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Identifies intrinsic types through CoreCLR's own object type rather than assembly-name guesses.
/// </summary>
internal sealed class ManagedCoreLibrary
{
    private readonly SourceBreakpointManager _modules;
    private int? _moduleId;

    /// <summary>
    /// Creates a core-library resolver for one runtime module catalog.
    /// </summary>
    internal ManagedCoreLibrary(SourceBreakpointManager modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _modules = modules;
    }

    /// <summary>
    /// Resolves an intrinsic signature to an exact definition in the target's core library.
    /// </summary>
    internal ManagedMetadataTypeSignature Resolve(ManagedMetadataTypeSignature signature, nint thread)
    {
        CorDebugLoadedModule module = GetModule(thread);
        using PEReader pe = module.OpenPeReader()
            ?? throw new InvalidOperationException("The runtime core library metadata is unavailable.");
        MetadataReader reader = pe.GetMetadataReader();
        var provider = new ManagedMetadataTypeSignatureProvider(module.Pointer);
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition definition = reader.GetTypeDefinition(handle);
            if (!definition.GetDeclaringType().IsNil ||
                reader.GetString(definition.Namespace) != "System" ||
                $"System.{reader.GetString(definition.Name)}" != signature.MetadataName)
            {
                continue;
            }

            ManagedMetadataTypeSignature resolved = provider.GetTypeFromDefinition(
                reader, handle, signature.IsValueType ? (byte)0x11 : (byte)0x12);
            return signature with
            {
                SourceModule = resolved.SourceModule,
                DefinitionToken = resolved.DefinitionToken,
                AssemblyReferenceToken = 0,
                AssemblyName = resolved.AssemblyName
            };
        }

        throw new InvalidOperationException($"Intrinsic type '{signature.MetadataName}' is unavailable.");
    }

    /// <summary>
    /// Gets the borrowed core-library module using a debugger-local null object without running target code.
    /// </summary>
    internal unsafe CorDebugLoadedModule GetModule(nint thread)
    {
        if (_moduleId is int moduleId && _modules.FindModule(moduleId) is { } cached)
        {
            return cached;
        }

        nint evaluation = 0;
        nint value = 0;
        nint value2 = 0;
        nint type = 0;
        nint runtimeClass = 0;
        nint module = 0;
        try
        {
            nint* evaluationAddress = &evaluation;
            CorDebugHResult.ThrowIfFailed(new ICorDebugThreadAbi(thread).CreateEval((nint)evaluationAddress),
                "ICorDebugThread.CreateEval");
            evaluation = RequirePointer(Volatile.Read(ref *evaluationAddress));
            nint* valueAddress = &value;
            // CreateValue(CLASS, null) creates a debugger-local null System.Object.
            // It neither invokes a method nor allocates an object in the target heap.
            CorDebugHResult.ThrowIfFailed(new ICorDebugEvalAbi(evaluation).CreateValue(0x12, 0, (nint)valueAddress),
                "ICorDebugEval.CreateValue");
            value = RequirePointer(Volatile.Read(ref *valueAddress));
            value2 = ComAbi.QueryInterface(value, ICorDebugValue2Abi.InterfaceId);
            nint* typeAddress = &type;
            CorDebugHResult.ThrowIfFailed(new ICorDebugValue2Abi(value2).GetExactType((nint)typeAddress),
                "ICorDebugValue2.GetExactType");
            type = RequirePointer(Volatile.Read(ref *typeAddress));
            nint* classAddress = &runtimeClass;
            CorDebugHResult.ThrowIfFailed(new ICorDebugTypeAbi(type).GetClass((nint)classAddress),
                "ICorDebugType.GetClass");
            runtimeClass = RequirePointer(Volatile.Read(ref *classAddress));
            nint* moduleAddress = &module;
            CorDebugHResult.ThrowIfFailed(new ICorDebugClassAbi(runtimeClass).GetModule((nint)moduleAddress),
                "ICorDebugClass.GetModule");
            module = RequirePointer(Volatile.Read(ref *moduleAddress));
            CorDebugLoadedModule loaded = _modules.FindModule(module)
                ?? throw new InvalidOperationException("The runtime core library is not registered.");
            _moduleId = loaded.Id;
            return loaded;
        }
        finally
        {
            Release(module);
            Release(runtimeClass);
            Release(type);
            Release(value2);
            Release(value);
            Release(evaluation);
        }
    }

    private static nint RequirePointer(nint pointer) => pointer != 0
        ? pointer : throw new InvalidOperationException("The runtime returned no intrinsic type reference.");

    private static void Release(nint pointer)
    {
        if (pointer != 0)
        {
            _ = ComAbi.Release(pointer);
        }
    }
}
