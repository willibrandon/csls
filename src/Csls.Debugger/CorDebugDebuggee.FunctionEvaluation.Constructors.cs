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
    private ManagedConstructorBinding ResolveConstructor(
        string typeName,
        DebugExpressionLanguage language,
        ManagedExpressionValue[] arguments,
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

        MetadataReader metadata = peReader.GetMetadataReader();
        TypeDefinition type = metadata.GetTypeDefinition(
            System.Reflection.Metadata.Ecma335.MetadataTokens.TypeDefinitionHandle(
                checked((int)(typeToken & 0x00FFFFFF))));
        int expectedTypeArgumentCount = type.GetGenericParameters().Count;
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

        uint? constructorToken = TryResolveDeclaredMethod(
            metadata,
            typeToken,
            ".ctor",
            language,
            arguments,
            staticMethod: false,
            declaringTypeArguments: runtimeType.TypeArguments.Select(
                static argument => argument.DebuggerTypeName).ToArray());
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

            function = GetModuleFunction(module.Pointer, constructorToken.Value);
            return new ManagedConstructorBinding(function, typeArguments);
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
