using Csls.Protocol;

namespace Csls.EndToEndPerformance;

/// <summary>
/// Identifies the real source document used for protocol measurements.
/// </summary>
internal sealed class PerformanceDocument
{
    /// <summary>
    /// Gets the absolute source-document path.
    /// </summary>
    internal required string Path { get; init; }

    /// <summary>
    /// Gets the exact source text opened through LSP.
    /// </summary>
    internal required string Text { get; init; }

    /// <summary>
    /// Gets a valid identifier position used for semantic requests.
    /// </summary>
    internal required Position Position { get; init; }

    /// <summary>
    /// Gets the owning Roslyn project name.
    /// </summary>
    internal required string ProjectName { get; init; }
}
