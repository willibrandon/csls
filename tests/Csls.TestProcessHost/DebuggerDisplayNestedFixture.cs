namespace Csls.TestProcessHost;

/// <summary>
/// Provides one nested field used by debugger display expression traversal.
/// </summary>
internal sealed class DebuggerDisplayNestedFixture
{
    /// <summary>
    /// Stores the nested value referenced by the parent display template.
    /// </summary>
    internal readonly int _value = 55;
}
