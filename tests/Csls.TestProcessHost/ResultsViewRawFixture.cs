using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Produces an ordinary Raw View while remaining eligible for lazy enumeration.
/// </summary>
internal sealed class ResultsViewRawFixture : ResultsViewFixture<int>
{
    /// <summary>
    /// Retains a field visible only through the ordinary Raw View.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal readonly int _hidden = 173;

    /// <summary>
    /// Creates distinct collection contents for default and raw-view inspection.
    /// </summary>
    internal ResultsViewRawFixture() : base([171, 172])
    {
    }
}
