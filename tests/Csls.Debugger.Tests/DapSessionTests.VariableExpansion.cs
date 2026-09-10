using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Provides DAP variable-expansion assertions over the real debugger process.
/// </summary>
public sealed partial class DapSessionTests
{
    private static void WriteVariablePagingInitializeArguments(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("supportsVariablePaging", true);
        writer.WriteBoolean("supportsInvalidatedEvent", true);
        writer.WriteEndObject();
    }

    private async Task<JsonElement[]> ReadVariablesAsync(
        DapTestClient client,
        int variablesReference,
        int? start = null,
        int? count = null)
    {
        int sequence = await client.SendRequestAsync(
            "variables",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("variablesReference", variablesReference);
                if (start is not null)
                {
                    writer.WriteNumber("start", start.Value);
                }

                if (count is not null)
                {
                    writer.WriteNumber("count", count.Value);
                }

                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "variables", success: true);
        return
        [
            .. response.RootElement
                .GetProperty("body")
                .GetProperty("variables")
                .EnumerateArray()
                .Select(variable => variable.Clone())
        ];
    }
}
