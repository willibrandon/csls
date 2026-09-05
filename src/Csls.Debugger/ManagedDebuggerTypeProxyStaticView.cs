namespace Csls.Debugger;

/// <summary>
/// Identifies the synthetic static-member container for a constructed debugger proxy.
/// </summary>
/// <param name="VariablesReference">The generation-owned synthetic container identifier.</param>
internal sealed record ManagedDebuggerTypeProxyStaticView(int VariablesReference);
