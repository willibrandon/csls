using System.Text.Json;

namespace Csls.Cli.Worker;

/// <summary>
/// Wraps every machine-readable CLI result in the stable version-one response shape.
/// </summary>
internal sealed class CliResponseEnvelope
{
    /// <summary>
    /// Gets the response schema version.
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Gets the unique correlation identifier for this invocation.
    /// </summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Gets whether the requested operation completed successfully.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets the operation result or structured failure details.
    /// </summary>
    public JsonElement Data { get; init; }

    /// <summary>
    /// Gets the continuation cursor when another bounded result page is available.
    /// </summary>
    public string? NextCursor { get; init; }
}
