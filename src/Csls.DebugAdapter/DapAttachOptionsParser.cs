using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Validates a DAP request that attaches to one local managed process.
/// </summary>
internal static class DapAttachOptionsParser
{
    /// <summary>
    /// Parses the required process identifier and optional runtime behavior.
    /// </summary>
    /// <param name="arguments">The DAP attach arguments.</param>
    /// <returns>The validated attach configuration.</returns>
    internal static DapAttachConfiguration Parse(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("processId", out JsonElement processIdValue) ||
            !processIdValue.TryGetInt32(out int processId) ||
            processId <= 0)
        {
            throw new ArgumentException(
                "The attach request requires a positive integer processId.");
        }

        return new DapAttachConfiguration(
            processId,
            DapBooleanOptionParser.Get(arguments, "justMyCode", defaultValue: true));
    }
}
