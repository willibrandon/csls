namespace Csls.Cli.Worker;

/// <summary>
/// Describes the agent skill file created by the CLI.
/// </summary>
internal sealed class AgentInitResult
{
    /// <summary>
    /// Gets the absolute path of the created skill file.
    /// </summary>
    public required string OutputPath { get; init; }
}
