using Csls.Debugger.Contracts;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves explicitly requested object constructors from loaded managed modules.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private nint ResolveConstructor(
        string typeName,
        DebugExpressionLanguage language,
        ManagedExpressionValue[] arguments)
    {
        (CorDebugLoadedModule module, uint typeToken) = ResolveLoadedRuntimeType(
            typeName,
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
            staticMethod: false);
        if (constructorToken is null)
        {
            throw new InvalidOperationException(
                $"No instance constructor with {arguments.Length} argument(s) is available " +
                $"on runtime type '{typeName}'.");
        }

        return GetModuleFunction(module.Pointer, constructorToken.Value);
    }
}
