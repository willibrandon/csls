using Csls.Protocol;
using StreamJsonRpc;

namespace Csls.Rpc;

/// <summary>
/// Registers the LSP method table explicitly without assembly scanning.
/// </summary>
public static class LspMethodRegistry
{
    /// <summary>
    /// Registers every implemented LSP request and notification delegate.
    /// </summary>
    /// <param name="rpc">The StreamJsonRpc connection to configure.</param>
    /// <param name="target">The language server target implementation.</param>
    public static void Register(JsonRpc rpc, ILspRpcTarget target)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        ArgumentNullException.ThrowIfNull(target);

        AddParameterObjectMethod(
            rpc,
            "initialize",
            new Func<InitializeParams, CancellationToken, Task<InitializeResult>>(
                target.InitializeAsync));
        AddParameterObjectMethod(
            rpc,
            "initialized",
            new Func<InitializedParams, CancellationToken, Task>(target.InitializedAsync));
        rpc.AddLocalRpcMethod(
            "shutdown",
            new Func<CancellationToken, Task<object?>>(target.ShutdownAsync));
        rpc.AddLocalRpcMethod("exit", new Func<Task>(target.ExitAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/didOpen",
            new Func<DidOpenTextDocumentParams, CancellationToken, Task>(target.DidOpenAsync));
        AddParameterObjectMethod(
            rpc,
            "textDocument/hover",
            new Func<TextDocumentPositionParams, CancellationToken, Task<Hover?>>(target.HoverAsync));
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
                $"LSP method {methodName} requires an instance target."),
            attribute);
    }
}
