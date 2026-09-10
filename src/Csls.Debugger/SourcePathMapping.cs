namespace Csls.Debugger;

/// <summary>
/// Maps one normalized build-time source prefix to an editor-visible prefix.
/// </summary>
internal sealed class SourcePathMapping
{
    /// <summary>
    /// Gets the normalized build-time path prefix.
    /// </summary>
    internal required string BuildPath { get; init; }

    /// <summary>
    /// Gets the normalized local editor path prefix.
    /// </summary>
    internal required string LocalPath { get; init; }

    /// <summary>
    /// Gets the comparison appropriate for the build-time path platform.
    /// </summary>
    internal required StringComparison Comparison { get; init; }
}
