namespace Csls.TestProcessHost;

/// <summary>
/// Inherits debugger presentation metadata from its runtime base type.
/// </summary>
internal sealed class InheritedDebuggerDisplayFixture : InheritedDebuggerDisplayBaseFixture
{
    /// <summary>
    /// Stores a derived field used to prove ordinary expansion remains available.
    /// </summary>
    internal readonly int _derivedValue = 64;
}
