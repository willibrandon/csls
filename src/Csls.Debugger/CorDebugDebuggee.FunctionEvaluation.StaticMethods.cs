using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Resolves explicitly qualified static calls from loaded managed modules.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private ManagedFunctionBinding ResolveStaticFunction(
        DebugExpressionNode receiver,
        string methodName,
        DebugExpressionLanguage language,
        ManagedExpressionValue[] arguments,
        nint thread)
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
        uint? methodToken = ManagedFunctionMethodResolver.Resolve(
            resolvedModule,
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

        ManagedBoundType? resultType = _boundTypes.BindMethodResult(
            resolvedModule.Pointer, methodToken.Value, [], thread);
        return new ManagedFunctionBinding(GetModuleFunction(resolvedModule.Pointer, methodToken.Value), [], resultType);
    }

    private (CorDebugLoadedModule Module, uint TypeToken) ResolveLoadedRuntimeType(
        string typeName,
        DebugExpressionLanguage language,
        string operation) => _typeNames.Resolve(typeName, language, operation);

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

}
