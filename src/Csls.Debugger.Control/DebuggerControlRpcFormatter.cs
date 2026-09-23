using StreamJsonRpc;

namespace Csls.Debugger.Control;

/// <summary>
/// Creates the NativeAOT-safe formatter for private debugger control RPC.
/// </summary>
internal static class DebuggerControlRpcFormatter
{
    /// <summary>
    /// Creates a formatter owned by the caller.
    /// </summary>
    /// <returns>The private debugger control formatter.</returns>
    internal static NerdbankMessagePackFormatter Create() => new()
    {
        TypeShapeProvider = DebuggerRpcClient.GeneratedTypeShapeProvider
    };
}
