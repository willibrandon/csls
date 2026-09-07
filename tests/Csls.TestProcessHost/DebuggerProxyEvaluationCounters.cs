namespace Csls.TestProcessHost;

/// <summary>
/// Records real debugger proxy side effects independently in each fixture process.
/// </summary>
internal sealed class DebuggerProxyEvaluationCounters
{
    /// <summary>
    /// Gets the counter storage owned by this fixture process.
    /// </summary>
    internal static readonly DebuggerProxyEvaluationCounters s_current = new();

    /// <summary>
    /// Counts completed debugger proxy constructor entries.
    /// </summary>
    public int Constructions;

    /// <summary>
    /// Counts computed property getter entries.
    /// </summary>
    public int GetterCalls;
}
