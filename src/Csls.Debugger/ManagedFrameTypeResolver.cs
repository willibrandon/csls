using Csls.Debugger.Interop;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves declared frame-slot types independently of the objects stored in those slots.
/// </summary>
internal sealed class ManagedFrameTypeResolver
{
    private readonly SourceBreakpointManager _modules;
    private readonly ManagedBoundTypeSystem _types;

    /// <summary>
    /// Creates a frame declaration resolver using the shared runtime type system.
    /// </summary>
    internal ManagedFrameTypeResolver(SourceBreakpointManager modules, ManagedBoundTypeSystem types)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(types);
        _modules = modules;
        _types = types;
    }

    /// <summary>
    /// Binds one active local or argument to its declaration and exact generic context.
    /// </summary>
    internal ManagedBoundType Resolve(ManagedFrameHandle frame, ManagedScopeKind scope, int index, nint thread)
    {
        CorDebugLoadedModule module = frame.ModuleId is int moduleId
            ? _modules.FindModule(moduleId) ?? throw new InvalidOperationException("The frame module has unloaded.")
            : throw new InvalidOperationException("The frame has no resolved module identity.");
        using PEReader pe = frame.OpenPeReader()
            ?? throw new InvalidOperationException("The frame declaration metadata is unavailable.");
        MetadataReader reader = pe.GetMetadataReader();
        using var metadata = new ManagedMetadataImage(reader, frame.MetadataDeltas);
        var methodHandle = (MethodDefinitionHandle)MetadataTokens.EntityHandle(checked((int)frame.MethodToken));
        MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
        TypeDefinitionHandle declaringType = metadata.GetDeclaringType(methodHandle);
        MethodSignature<ManagedMetadataTypeSignature> methodSignature = metadata.DecodeMethodSignature(methodHandle, module.Pointer);
        var provider = new ManagedMetadataTypeSignatureProvider(module.Pointer, metadata);
        ManagedMetadataTypeSignature signature;
        int typeArity = metadata.GetGenericParameterCount(declaringType);
        if (scope == ManagedScopeKind.Locals)
        {
            StandaloneSignatureHandle handle = GetLocalSignature(frame.Pointer);
            (MetadataReader owner, EntityHandle relative) = metadata.Resolve(handle);
            BlobReader blob = metadata.GetBlobReader(owner.GetStandaloneSignature((StandaloneSignatureHandle)relative).Signature);
            var decoder = new SignatureDecoder<ManagedMetadataTypeSignature, object?>(provider, reader, genericContext: null);
            ImmutableArray<ManagedMetadataTypeSignature> locals = decoder.DecodeLocalSignature(ref blob);
            if ((uint)index >= (uint)locals.Length)
            {
                throw new InvalidOperationException("The local slot is outside its declaration signature.");
            }

            signature = locals[index];
        }
        else
        {
            bool hasThis = (method.Attributes & MethodAttributes.Static) == 0;
            if (hasThis && index == 0)
            {
                signature = provider.GetTypeFromDefinition(reader, declaringType, 0x12) with
                {
                    TypeArguments = [.. Enumerable.Range(0, typeArity)
                        .Select(parameter => provider.GetGenericTypeParameter(null, parameter))]
                };
            }
            else
            {
                ImmutableArray<ManagedMetadataTypeSignature> parameters = methodSignature.ParameterTypes;
                int parameterIndex = index - (hasThis ? 1 : 0);
                if ((uint)parameterIndex >= (uint)parameters.Length)
                {
                    throw new InvalidOperationException("The argument slot is outside its declaration signature.");
                }

                signature = parameters[parameterIndex];
            }
        }

        if (signature.UnsupportedKind == "by-reference")
        {
            signature = signature with { UnsupportedKind = null };
        }

        nint[] arguments = ManagedRuntimeTypeArguments.RetainFrame(frame.Pointer);
        try
        {
            if (arguments.Length != typeArity + methodSignature.GenericParameterCount)
            {
                throw new InvalidOperationException("The frame's runtime generic arguments do not match its declaration.");
            }

            ManagedBoundType[] bound = [.. arguments.Select(argument => _types.CaptureType(argument, thread))];
            return _types.Bind(signature, bound[..typeArity], bound[typeArity..], thread);
        }
        finally
        {
            foreach (nint argument in arguments)
            {
                _ = ComAbi.Release(argument);
            }
        }
    }

    private static unsafe StandaloneSignatureHandle GetLocalSignature(nint frame)
    {
        nint function = 0;
        try
        {
            nint* functionAddress = &function;
            CorDebugHResult.ThrowIfFailed(new ICorDebugFrameAbi(frame).GetFunction((nint)functionAddress),
                "ICorDebugFrame.GetFunction");
            function = Volatile.Read(ref *functionAddress);
            if (function == 0)
            {
                throw new InvalidOperationException("The current frame has no runtime function.");
            }

            uint token = 0;
            uint* tokenAddress = &token;
            CorDebugHResult.ThrowIfFailed(new ICorDebugFunctionAbi(function).GetLocalVarSigToken((nint)tokenAddress),
                "ICorDebugFunction.GetLocalVarSigToken");
            StandaloneSignatureHandle handle = MetadataTokens.StandaloneSignatureHandle(
                checked((int)(Volatile.Read(ref *tokenAddress) & 0x00ffffff)));
            if (handle.IsNil)
            {
                throw new InvalidOperationException("The current method version has no local declaration signature.");
            }

            return handle;
        }
        finally
        {
            if (function != 0)
            {
                _ = ComAbi.Release(function);
            }
        }
    }
}
