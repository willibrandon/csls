namespace Csls.Control.Contracts;

/// <summary>
/// Describes one compiler or analyzer diagnostic exposed by the control protocol.
/// </summary>
public sealed class ControlDiagnosticInfo
{
    /// <summary>
    /// Gets the compiler or analyzer diagnostic identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the Roslyn diagnostic severity name.
    /// </summary>
    public required string Severity { get; init; }

    /// <summary>
    /// Gets the invariant diagnostic message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Gets the owning project name.
    /// </summary>
    public required string ProjectName { get; init; }

    /// <summary>
    /// Gets the absolute source path when the diagnostic has a source location.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Gets the zero-based source line when one exists.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// Gets the zero-based source character when one exists.
    /// </summary>
    public int? Character { get; init; }
}
