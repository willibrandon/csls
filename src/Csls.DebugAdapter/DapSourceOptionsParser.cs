using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Parses source mapping and Source Link launch options.
/// </summary>
internal static class DapSourceOptionsParser
{
    /// <summary>
    /// Parses build-time source path mappings.
    /// </summary>
    /// <param name="arguments">The DAP launch arguments.</param>
    /// <returns>The validated source path mappings.</returns>
    internal static Dictionary<string, string> ParseSourceFileMap(JsonElement arguments)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (!arguments.TryGetProperty("sourceFileMap", out JsonElement mappings))
        {
            return result;
        }

        if (mappings.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The launch sourceFileMap value must be an object.");
        }

        foreach (JsonProperty property in mappings.EnumerateObject())
        {
            string localPath = GetString(property.Value, "sourceFileMap");
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                throw new ArgumentException(
                    "The launch sourceFileMap keys and values must be absolute paths.");
            }

            result.Add(property.Name, localPath);
        }

        return result;
    }

    /// <summary>
    /// Parses established Source Link URL enablement rules.
    /// </summary>
    /// <param name="arguments">The DAP launch arguments.</param>
    /// <returns>URL patterns mapped to enabled states.</returns>
    internal static Dictionary<string, bool> ParseSourceLinkOptions(JsonElement arguments)
    {
        Dictionary<string, bool> result = new(StringComparer.OrdinalIgnoreCase);
        if (!arguments.TryGetProperty("sourceLinkOptions", out JsonElement options))
        {
            return result;
        }

        if (options.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The launch sourceLinkOptions value must be an object.");
        }

        foreach (JsonProperty property in options.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object ||
                !property.Value.TryGetProperty("enabled", out JsonElement enabled) ||
                enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new ArgumentException(
                    $"The Source Link rule '{property.Name}' requires a Boolean enabled value.");
            }

            result.Add(property.Name, enabled.GetBoolean());
        }

        return result;
    }

    private static string GetString(JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException(
                $"The launch '{propertyName}' value must be a non-empty string.");
        }

        return value.GetString()!;
    }
}
