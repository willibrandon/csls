using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves explicitly requested object constructors from loaded managed modules.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private ManagedFunctionBinding ResolveConstructor(
        string typeName,
        DebugExpressionLanguage language,
        ManagedBoundType?[] arguments,
        nint thread)
    {
        ManagedRuntimeTypeReference runtimeType = ManagedRuntimeTypeNameParser.Parse(
            typeName,
            language);
        (CorDebugLoadedModule module, uint typeToken) = ResolveLoadedRuntimeType(
            runtimeType.MetadataName,
            language,
            "object construction");
        using PEReader? peReader = module.OpenPeReader();
        if (peReader is null)
        {
            throw new InvalidOperationException(
                $"Loaded module '{module.Name ?? "unnamed module"}' no longer has a " +
                "readable PE image.");
        }

        using var metadata = new ManagedMetadataImage(peReader.GetMetadataReader(), module.MetadataDeltas);
        TypeDefinitionHandle typeHandle = System.Reflection.Metadata.Ecma335.MetadataTokens.TypeDefinitionHandle(
            checked((int)(typeToken & 0x00FFFFFF)));
        TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
        int expectedTypeArgumentCount = metadata.GetGenericParameterCount(typeHandle);
        if (expectedTypeArgumentCount != runtimeType.TypeArguments.Count)
        {
            throw new InvalidOperationException(
                $"Runtime type '{runtimeType.DebuggerTypeName}' requires " +
                $"{expectedTypeArgumentCount} generic argument(s), but " +
                $"{runtimeType.TypeArguments.Count} were supplied.");
        }

        if ((type.Attributes & TypeAttributes.Abstract) != 0)
        {
            throw new InvalidOperationException(
                $"Runtime type '{typeName}' is abstract and cannot be constructed.");
        }

        ManagedBoundType[] boundTypeArguments = [.. runtimeType.TypeArguments.Select(
            argument => _boundTypes.BindName(argument.DebuggerTypeName, language, thread))];
        uint? constructorToken = ManagedFunctionMethodResolver.Resolve(
            metadata,
            module.Pointer,
            typeToken,
            ".ctor",
            language,
            arguments,
            staticMethod: false,
            _boundTypes,
            thread,
            declaringTypeArguments: boundTypeArguments);
        if (constructorToken is null)
        {
            throw new InvalidOperationException(
                $"No instance constructor with {arguments.Length} argument(s) is available " +
                $"on runtime type '{typeName}'.");
        }

        nint[] typeArguments = new nint[runtimeType.TypeArguments.Count];
        nint function = 0;
        try
        {
            for (int index = 0; index < typeArguments.Length; index++)
            {
                typeArguments[index] = ResolveRuntimeType(
                    runtimeType.TypeArguments[index],
                    language,
                    thread,
                    "object construction");
            }

            ManagedBoundType[] boundArguments = [.. typeArguments.Select(argument => _boundTypes.CaptureType(argument, thread))];
            ManagedBoundType? resultType = _boundTypes.BindMethodResult(
                module.Pointer, constructorToken.Value, boundArguments, thread, constructsObject: true);
            function = GetModuleFunction(module.Pointer, constructorToken.Value);
            return new ManagedFunctionBinding(function, typeArguments, resultType);
        }
        catch
        {
            if (function != 0)
            {
                _ = ComAbi.Release(function);
            }

            foreach (nint typeArgument in typeArguments.Where(
                static typeArgument => typeArgument != 0))
            {
                _ = ComAbi.Release(typeArgument);
            }

            throw;
        }
    }
}
