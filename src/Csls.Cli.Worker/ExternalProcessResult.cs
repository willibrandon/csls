namespace Csls.Cli.Worker;

/// <summary>
/// Describes one completed external process and its bounded captured output.
/// </summary>
internal sealed class ExternalProcessResult
{
    /// <summary>
    /// Gets the completed process exit code.
    /// </summary>
    public int ExitCode { get; init; }

    /// <summary>
    /// Gets the bounded standard output.
    /// </summary>
    public required string StandardOutput { get; init; }

    /// <summary>
    /// Gets the bounded standard error.
    /// </summary>
    public required string StandardError { get; init; }

    /// <summary>
    /// Gets whether either output stream exceeded its capture bound.
    /// </summary>
    public bool OutputTruncated { get; init; }
}
