namespace Csls.Debugger;

/// <summary>
/// Describes one validated debugger type-proxy declaration from target metadata.
/// </summary>
/// <param name="ProxyTypeName">The assembly-qualified reflection name of the proxy type.</param>
internal sealed record ManagedDebuggerTypeProxyAttribute(string ProxyTypeName);
