using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Finds metadata tokens excluded from source stepping by managed debugger conventions.
/// </summary>
internal static class ManagedStepFilterClassifier
{
    private const int MaximumExcludedTokenCount = 65_536;
    private const string DebuggerHidden = "System.Diagnostics.DebuggerHiddenAttribute";
    private const string DebuggerNonUserCode =
        "System.Diagnostics.DebuggerNonUserCodeAttribute";
    private const string DebuggerStepThrough =
        "System.Diagnostics.DebuggerStepThroughAttribute";

    /// <summary>
    /// Gets type and method tokens excluded by attributes and step-filtering policy.
    /// </summary>
    /// <param name="peReader">The readable managed module image.</param>
    /// <param name="justMyCode">Whether non-user-code attributes are honored.</param>
    /// <param name="enableStepFiltering">Whether properties and operators are skipped.</param>
    /// <returns>The bounded distinct metadata-token sequence.</returns>
    internal static uint[] GetExcludedTokens(
        PEReader peReader,
        bool justMyCode,
        bool enableStepFiltering)
    {
        ArgumentNullException.ThrowIfNull(peReader);
        MetadataReader reader = peReader.GetMetadataReader();
        var tokens = new HashSet<uint>();
        foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(typeHandle);
            if (HasTypeStepFilter(reader, type, justMyCode))
            {
                AddToken(tokens, MetadataTokens.GetToken(typeHandle));
                continue;
            }

            HashSet<MethodDefinitionHandle>? propertyAccessors = enableStepFiltering
                ? GetPropertyAccessors(reader, type)
                : null;
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                if (HasMethodStepFilter(reader, method, justMyCode) ||
                    propertyAccessors?.Contains(methodHandle) == true ||
                    enableStepFiltering && IsOperator(reader, method))
                {
                    AddToken(tokens, MetadataTokens.GetToken(methodHandle));
                }
            }
        }

        return [.. tokens.Order()];
    }

    private static HashSet<MethodDefinitionHandle> GetPropertyAccessors(
        MetadataReader reader,
        TypeDefinition type)
    {
        var result = new HashSet<MethodDefinitionHandle>();
        foreach (PropertyDefinitionHandle handle in type.GetProperties())
        {
            PropertyAccessors accessors = reader.GetPropertyDefinition(handle).GetAccessors();
            if (!accessors.Getter.IsNil)
            {
                _ = result.Add(accessors.Getter);
            }

            if (!accessors.Setter.IsNil)
            {
                _ = result.Add(accessors.Setter);
            }

            foreach (MethodDefinitionHandle other in accessors.Others)
            {
                _ = result.Add(other);
            }
        }

        return result;
    }

    private static bool HasTypeStepFilter(
        MetadataReader reader,
        TypeDefinition type,
        bool justMyCode) =>
        HasAttribute(reader, type.GetCustomAttributes(), DebuggerStepThrough) ||
        justMyCode && HasAttribute(
            reader,
            type.GetCustomAttributes(),
            DebuggerNonUserCode);

    private static bool HasMethodStepFilter(
        MetadataReader reader,
        MethodDefinition method,
        bool justMyCode) =>
        HasAttribute(reader, method.GetCustomAttributes(), DebuggerHidden) ||
        HasAttribute(reader, method.GetCustomAttributes(), DebuggerStepThrough) ||
        justMyCode && HasAttribute(
            reader,
            method.GetCustomAttributes(),
            DebuggerNonUserCode);

    private static bool HasAttribute(
        MetadataReader reader,
        CustomAttributeHandleCollection attributes,
        string expectedName)
    {
        foreach (CustomAttributeHandle handle in attributes)
        {
            if (string.Equals(
                GetAttributeTypeName(reader, reader.GetCustomAttribute(handle)),
                expectedName,
                StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetAttributeTypeName(
        MetadataReader reader,
        CustomAttribute attribute)
    {
        EntityHandle typeHandle = attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference =>
                reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
            HandleKind.MethodDefinition => reader
                .GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor)
                .GetDeclaringType(),
            _ => default
        };
        return typeHandle.Kind switch
        {
            HandleKind.TypeReference => GetTypeName(
                reader,
                reader.GetTypeReference((TypeReferenceHandle)typeHandle)),
            HandleKind.TypeDefinition => GetTypeName(
                reader,
                reader.GetTypeDefinition((TypeDefinitionHandle)typeHandle)),
            _ => null
        };
    }

    private static string GetTypeName(MetadataReader reader, TypeReference type) =>
        JoinTypeName(reader.GetString(type.Namespace), reader.GetString(type.Name));

    private static string GetTypeName(MetadataReader reader, TypeDefinition type) =>
        JoinTypeName(reader.GetString(type.Namespace), reader.GetString(type.Name));

    private static string JoinTypeName(string namespaceName, string name) =>
        string.IsNullOrEmpty(namespaceName) ? name : $"{namespaceName}.{name}";

    private static bool IsOperator(MetadataReader reader, MethodDefinition method) =>
        reader.GetString(method.Name).StartsWith("op_", StringComparison.Ordinal);

    private static void AddToken(HashSet<uint> tokens, int token)
    {
        if (tokens.Count == MaximumExcludedTokenCount)
        {
            throw new InvalidDataException(
                $"The module exceeds the step-filter token limit of {MaximumExcludedTokenCount}.");
        }

        _ = tokens.Add(checked((uint)token));
    }
}
