using Csls.Debugger.Contracts;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves explicitly qualified static calls from loaded managed modules.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private const int MaximumFunctionEvaluationTypeScanCount = 1_000_000;

    private nint ResolveStaticFunction(
        DebugExpressionNode receiver,
        string methodName,
        DebugExpressionLanguage language,
        ManagedExpressionValue[] arguments)
    {
        if (!TryGetQualifiedTypeName(receiver, out string typeName))
        {
            throw new InvalidOperationException(
                "A static method call requires an explicitly qualified type receiver.");
        }

        (CorDebugLoadedModule resolvedModule, uint typeToken) = ResolveLoadedRuntimeType(
            typeName,
            language,
            "static call");
        using PEReader? resolvedReader = resolvedModule.OpenPeReader();
        if (resolvedReader is null)
        {
            throw new InvalidOperationException(
                $"Loaded module '{resolvedModule.Name ?? "unnamed module"}' no longer has " +
                "a readable PE image.");
        }

        uint? methodToken = TryResolveDeclaredMethod(
            resolvedReader.GetMetadataReader(),
            typeToken,
            methodName,
            language,
            arguments,
            staticMethod: true);
        if (methodToken is null)
        {
            throw new InvalidOperationException(
                $"No static method named '{methodName}' with {arguments.Length} argument(s) " +
                $"is available on runtime type '{typeName}'.");
        }

        return GetModuleFunction(resolvedModule.Pointer, methodToken.Value);
    }

    private (CorDebugLoadedModule Module, uint TypeToken) ResolveLoadedRuntimeType(
        string typeName,
        DebugExpressionLanguage language,
        string operation)
    {
        StringComparison comparison = language == DebugExpressionLanguage.VisualBasic
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        bool simpleName = !typeName.Contains('.', StringComparison.Ordinal) &&
            !typeName.Contains('+', StringComparison.Ordinal);
        var matches = new List<(CorDebugLoadedModule Module, uint TypeToken)>();
        int scannedTypeCount = 0;
        foreach (CorDebugLoadedModule module in _sourceBreakpoints.GetRuntimeModules())
        {
            scannedTypeCount = AddFunctionEvaluationTypeMatches(
                module,
                typeName,
                simpleName,
                comparison,
                matches,
                scannedTypeCount);
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"No loaded runtime type named '{typeName}' is available for {operation}.");
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Runtime type name '{typeName}' is ambiguous across loaded modules for " +
                $"{operation}. Use its fully qualified metadata name.");
        }

        return matches[0];
    }

    private static int AddFunctionEvaluationTypeMatches(
        CorDebugLoadedModule module,
        string typeName,
        bool simpleName,
        StringComparison comparison,
        List<(CorDebugLoadedModule Module, uint TypeToken)> matches,
        int scannedTypeCount)
    {
        using PEReader? peReader = module.OpenPeReader();
        if (peReader is null || !peReader.HasMetadata)
        {
            return scannedTypeCount;
        }

        MetadataReader metadata = peReader.GetMetadataReader();
        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
        {
            if (++scannedTypeCount > MaximumFunctionEvaluationTypeScanCount)
            {
                throw new InvalidOperationException(
                    $"Static method binding exceeds the loaded-type scan limit of " +
                    $"{MaximumFunctionEvaluationTypeScanCount}.");
            }

            TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
            string candidateName = simpleName
                ? metadata.GetString(type.Name)
                : GetFunctionEvaluationTypeName(metadata, typeHandle);
            if (string.Equals(candidateName, typeName, comparison))
            {
                matches.Add((
                    module,
                    checked((uint)MetadataTokens.GetToken(typeHandle))));
            }
        }

        return scannedTypeCount;
    }

    private static bool TryGetQualifiedTypeName(
        DebugExpressionNode node,
        out string typeName)
    {
        if (node.Kind == DebugExpressionNodeKind.Identifier &&
            !string.IsNullOrWhiteSpace(node.Text))
        {
            typeName = node.Text;
            return true;
        }

        if (node.Kind == DebugExpressionNodeKind.MemberAccess &&
            node.Children.Count == 1 &&
            !string.IsNullOrWhiteSpace(node.Text) &&
            TryGetQualifiedTypeName(node.Children[0], out string containingName))
        {
            typeName = $"{containingName}.{node.Text}";
            return true;
        }

        typeName = string.Empty;
        return false;
    }

    private static string GetFunctionEvaluationTypeName(
        MetadataReader metadata,
        TypeDefinitionHandle handle)
    {
        TypeDefinition type = metadata.GetTypeDefinition(handle);
        string name = metadata.GetString(type.Name);
        TypeDefinitionHandle declaringType = type.GetDeclaringType();
        if (!declaringType.IsNil)
        {
            return $"{GetFunctionEvaluationTypeName(metadata, declaringType)}+{name}";
        }

        string @namespace = metadata.GetString(type.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }
}
