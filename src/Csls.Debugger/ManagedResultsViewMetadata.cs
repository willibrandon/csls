using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Validates the runtime debug-view constructor and Items getter metadata contracts.
/// </summary>
internal static class ManagedResultsViewMetadata
{
    /// <summary>
    /// Finds the exact public constructor and Items getter for one debug-view definition.
    /// </summary>
    /// <param name="module">The loaded module defining the debug view.</param>
    /// <param name="typeToken">The exact debug-view type-definition token.</param>
    /// <param name="isGeneric">Whether the debug view has an enumerable element parameter.</param>
    /// <param name="constructorToken">Receives the constructor method token.</param>
    /// <param name="itemsGetterToken">Receives the Items getter method token.</param>
    /// <returns>True when both members satisfy the expected runtime signatures.</returns>
    internal static bool TryGetMembers(
        CorDebugLoadedModule module,
        uint typeToken,
        bool isGeneric,
        out uint constructorToken,
        out uint itemsGetterToken)
    {
        constructorToken = 0;
        itemsGetterToken = 0;
        using PEReader? peReader = module.OpenPeReader();
        if (peReader is null || !peReader.HasMetadata)
        {
            return false;
        }

        MetadataReader metadata = peReader.GetMetadataReader();
        TypeDefinition type = metadata.GetTypeDefinition(
            MetadataTokens.TypeDefinitionHandle(checked((int)(typeToken & 0x00FFFFFF))));
        if (type.GetGenericParameters().Count != (isGeneric ? 1 : 0))
        {
            return false;
        }

        foreach (MethodDefinitionHandle handle in type.GetMethods())
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if (!IsPublicInstance(method) ||
                !string.Equals(metadata.GetString(method.Name), ".ctor", StringComparison.Ordinal))
            {
                continue;
            }

            MethodSignature<ManagedMetadataTypeSignature> signature = Decode(method);
            if (!signature.Header.IsGeneric && signature.ParameterTypes.Length == 1 &&
                IsNamedType(signature.ReturnType, "System.Void") &&
                IsEnumerableParameter(signature.ParameterTypes[0], isGeneric))
            {
                if (constructorToken != 0)
                {
                    return false;
                }

                constructorToken = checked((uint)MetadataTokens.GetToken(handle));
            }
        }

        foreach (PropertyDefinitionHandle handle in type.GetProperties())
        {
            PropertyDefinition property = metadata.GetPropertyDefinition(handle);
            if (!string.Equals(metadata.GetString(property.Name), "Items", StringComparison.Ordinal))
            {
                continue;
            }

            MethodDefinitionHandle getterHandle = property.GetAccessors().Getter;
            if (getterHandle.IsNil || itemsGetterToken != 0)
            {
                return false;
            }

            MethodDefinition getter = metadata.GetMethodDefinition(getterHandle);
            MethodSignature<ManagedMetadataTypeSignature> signature = Decode(getter);
            ManagedMetadataTypeSignature result = signature.ReturnType;
            if (!IsPublicInstance(getter) || signature.Header.IsGeneric ||
                signature.ParameterTypes.Length != 0 ||
                result.ArrayShapes is not [{ Rank: 1, IsVector: true }] ||
                !(isGeneric ? IsElementParameter(result) : IsObjectElement(result)))
            {
                return false;
            }

            itemsGetterToken = checked((uint)MetadataTokens.GetToken(getterHandle));
        }

        return constructorToken != 0 && itemsGetterToken != 0;
    }

    private static MethodSignature<ManagedMetadataTypeSignature> Decode(MethodDefinition method) =>
        method.DecodeSignature(ManagedMetadataTypeSignatureProvider.Instance, genericContext: null);

    private static bool IsPublicInstance(MethodDefinition method) =>
        (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public &&
        (method.Attributes & MethodAttributes.Static) == 0;

    private static bool IsEnumerableParameter(ManagedMetadataTypeSignature type, bool isGeneric) =>
        type.ArrayShapes.Count == 0 && !type.IsValueType &&
        (isGeneric
            ? string.Equals(type.MetadataName, "System.Collections.Generic.IEnumerable`1", StringComparison.Ordinal) &&
                type.TypeArguments is [ManagedMetadataTypeSignature argument] &&
                IsElementParameter(argument) && argument.ArrayShapes.Count == 0
            : IsNamedType(type, "System.Collections.IEnumerable") && type.TypeArguments.Count == 0);

    private static bool IsElementParameter(ManagedMetadataTypeSignature type) =>
        type.GenericTypeParameterIndex == 0 && type.TypeArguments.Count == 0 && type.MetadataName is null;

    private static bool IsObjectElement(ManagedMetadataTypeSignature type) =>
        string.Equals(type.MetadataName, "System.Object", StringComparison.Ordinal) &&
        type.GenericTypeParameterIndex is null && type.TypeArguments.Count == 0 && !type.IsValueType;

    private static bool IsNamedType(ManagedMetadataTypeSignature type, string name) =>
        string.Equals(type.MetadataName, name, StringComparison.Ordinal) &&
        type.GenericTypeParameterIndex is null && type.ArrayShapes.Count == 0;
}
