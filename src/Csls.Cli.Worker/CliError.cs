namespace Csls.Cli.Worker;

/// <summary>
/// Describes a machine-readable failure returned by a csls CLI operation.
/// </summary>
internal sealed class CliError
{
    /// <summary>
    /// Gets the stable error category for automation.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Gets the actionable failure description.
    /// </summary>
    public required string Message { get; init; }
}
