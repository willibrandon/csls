using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Explicitly advertises the protocol events implemented by the real debugger test client.
/// </summary>
internal sealed partial class DapTestClient
{
    /// <summary>
    /// Initializes a valid client with explicit invalidation support and optional additional properties.
    /// </summary>
    /// <param name="cancellationToken">Cancels writing the initialize request.</param>
    /// <param name="supportsInvalidatedEvent">Whether invalidation events are supported, or null to omit the capability.</param>
    /// <param name="writeProperties">Writes additional properties inside the arguments object without its delimiters.</param>
    /// <returns>The assigned initialize request sequence number.</returns>
    internal Task<int> SendInitializeRequestAsync(
        CancellationToken cancellationToken,
        bool? supportsInvalidatedEvent = true,
        Action<Utf8JsonWriter>? writeProperties = null) => SendRequestAsync(
            "initialize",
            writer =>
            {
                writer.WriteStartObject();
                if (supportsInvalidatedEvent is bool supported)
                {
                    writer.WriteBoolean("supportsInvalidatedEvent", supported);
                }

                writeProperties?.Invoke(writer);
                writer.WriteEndObject();
            },
            cancellationToken);
}
