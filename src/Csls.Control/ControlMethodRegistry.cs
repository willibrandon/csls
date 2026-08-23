using Csls.Control.Contracts;
using Csls.Protocol;
using StreamJsonRpc;

namespace Csls.Control;

/// <summary>
/// Registers the versioned control method table explicitly without assembly scanning.
/// </summary>
internal static class ControlMethodRegistry
{
    /// <summary>
    /// Registers every implemented control request delegate.
    /// </summary>
    /// <param name="rpc">The StreamJsonRpc connection to configure.</param>
    /// <param name="target">The control target implementation.</param>
    internal static void Register(JsonRpc rpc, IControlRpcTarget target)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        ArgumentNullException.ThrowIfNull(target);
        rpc.AddLocalRpcMethod(
            ControlMethods.GetSession,
            new Func<CancellationToken, Task<ControlSessionInfo>>(target.GetSessionAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.GetDashboardSnapshot,
            new Func<ControlDashboardRequest, CancellationToken, Task<ControlDashboardSnapshot>>(
                target.GetDashboardSnapshotAsync));
        rpc.AddLocalRpcMethod(
            ControlMethods.RestoreWorkspace,
            new Func<CancellationToken, Task<ControlWorkspaceOperationResult>>(
                target.RestoreWorkspaceAsync));
        rpc.AddLocalRpcMethod(
            ControlMethods.ReloadWorkspace,
            new Func<CancellationToken, Task<ControlWorkspaceOperationResult>>(
                target.ReloadWorkspaceAsync));
        rpc.AddLocalRpcMethod(
            ControlMethods.RestartBuildHosts,
            new Func<CancellationToken, Task<ControlWorkspaceOperationResult>>(
                target.RestartBuildHostsAsync));
        rpc.AddLocalRpcMethod(
            ControlMethods.ClearCaches,
            new Func<CancellationToken, Task<ControlWorkspaceOperationResult>>(
                target.ClearCachesAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.GetHover,
            new Func<ControlHoverRequest, CancellationToken, Task<ControlHoverResult>>(
                target.GetHoverAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.GetDiagnostics,
            new Func<ControlDiagnosticRequest, CancellationToken, Task<DocumentDiagnosticReport>>(
                target.GetDiagnosticsAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.GetCompletion,
            new Func<ControlCompletionRequest, CancellationToken, Task<CompletionList>>(
                target.GetCompletionAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.GetDefinition,
            new Func<ControlNavigationRequest, CancellationToken, Task<IReadOnlyList<Location>>>(
                target.GetDefinitionAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.GetDeclaration,
            new Func<ControlNavigationRequest, CancellationToken, Task<IReadOnlyList<Location>>>(
                target.GetDeclarationAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.GetTypeDefinition,
            new Func<ControlNavigationRequest, CancellationToken, Task<IReadOnlyList<Location>>>(
                target.GetTypeDefinitionAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.GetImplementation,
            new Func<ControlNavigationRequest, CancellationToken, Task<IReadOnlyList<Location>>>(
                target.GetImplementationAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.GetSelectionRanges,
            new Func<ControlSelectionRangeRequest, CancellationToken, Task<IReadOnlyList<SelectionRange>>>(
                target.GetSelectionRangesAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.GetDocumentHighlights,
            new Func<ControlNavigationRequest, CancellationToken, Task<IReadOnlyList<DocumentHighlight>>>(
                target.GetDocumentHighlightsAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.GetReferences,
            new Func<ControlNavigationRequest, CancellationToken, Task<IReadOnlyList<Location>>>(
                target.GetReferencesAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.GetDocumentSymbols,
            new Func<ControlDocumentRequest, CancellationToken, Task<IReadOnlyList<DocumentSymbol>>>(
                target.GetDocumentSymbolsAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.GetWorkspaceSymbols,
            new Func<ControlWorkspaceSymbolRequest, CancellationToken, Task<IReadOnlyList<WorkspaceSymbol>>>(
                target.GetWorkspaceSymbolsAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.ResolveWorkspaceSymbol,
            new Func<WorkspaceSymbol, CancellationToken, Task<WorkspaceSymbol>>(
                target.ResolveWorkspaceSymbolAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.GetSignatureHelp,
            new Func<ControlSignatureHelpRequest, CancellationToken, Task<SignatureHelp?>>(
                target.GetSignatureHelpAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.PreviewRename,
            new Func<ControlRenameRequest, CancellationToken, Task<ControlEditPlan>>(
                target.PreviewRenameAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.PreviewFormatting,
            new Func<ControlFormattingRequest, CancellationToken, Task<ControlEditPlan>>(
                target.PreviewFormattingAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.GetCodeActions,
            new Func<ControlCodeActionRequest, CancellationToken, Task<IReadOnlyList<ControlCodeActionPlan>>>(
                target.GetCodeActionsAsync));
        AddParameterObjectMethod(
            rpc,
            ControlMethods.ApplyEditPlan,
            new Func<ControlApplyEditPlanRequest, CancellationToken, Task<ControlApplyEditPlanResult>>(
                target.ApplyEditPlanAsync));
    }

    private static void AddParameterObjectMethod(
        JsonRpc rpc,
        string methodName,
        Delegate handler)
    {
        var attribute = new JsonRpcMethodAttribute(methodName)
        {
            UseSingleObjectParameterDeserialization = true
        };
        rpc.AddLocalRpcMethod(
            handler.Method,
            handler.Target ?? throw new InvalidOperationException(
                $"Control method {methodName} requires an instance target."),
            attribute);
    }
}
