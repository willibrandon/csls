namespace Csls.Control.Contracts;

/// <summary>
/// Selects optional expensive data for one bounded dashboard snapshot.
/// </summary>
public sealed class ControlDashboardRequest
{
    /// <summary>
    /// Gets whether current compiler and analyzer diagnostics should be evaluated.
    /// </summary>
    public bool IncludeDiagnostics { get; init; }
}
