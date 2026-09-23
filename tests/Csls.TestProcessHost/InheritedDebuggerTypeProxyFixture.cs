namespace Csls.TestProcessHost;

/// <summary>
/// Inherits a debugger proxy declaration from its runtime base type.
/// </summary>
internal sealed class InheritedDebuggerTypeProxyFixture :
    InheritedDebuggerTypeProxyBaseFixture
{
    /// <summary>
    /// Stores a derived physical value omitted by the inherited projection.
    /// </summary>
    internal readonly int _derivedValue = 46;
}
