namespace Csls.Cli.Worker;

/// <summary>
/// Summarizes one Roslyn project loaded during a doctor inspection.
/// </summary>
internal sealed class DoctorProject
{
    /// <summary>
    /// Gets the project display name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the absolute project file path when one exists.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Gets the number of source documents in the project.
    /// </summary>
    public int DocumentCount { get; init; }

    /// <summary>
    /// Gets the number of configured analyzer references.
    /// </summary>
    public int AnalyzerReferenceCount { get; init; }
}
