namespace Csls.Debugger;

/// <summary>
/// Carries metadata and Portable PDB identity for one managed instruction pointer.
/// </summary>
internal sealed class ManagedFrameLocation
{
    /// <summary>
    /// Gets or initializes the language-neutral method display name.
    /// </summary>
    internal required string Name { get; init; }

    /// <summary>
    /// Gets or initializes the source document path when available.
    /// </summary>
    internal string? SourcePath { get; init; }

    /// <summary>
    /// Gets or initializes the one-based source line, or zero when unavailable.
    /// </summary>
    internal int Line { get; init; }

    /// <summary>
    /// Gets or initializes the one-based source column, or zero when unavailable.
    /// </summary>
    internal int Column { get; init; }

    /// <summary>
    /// Gets or initializes the loaded module path used for metadata lookup.
    /// </summary>
    internal string? ModulePath { get; init; }
}
