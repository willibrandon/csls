using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Parses strict optional boolean values shared by DAP target configurations.
/// </summary>
internal static class DapBooleanOptionParser
{
    /// <summary>
    /// Gets a strict boolean property or its documented default.
    /// </summary>
    /// <param name="owner">The launch or attach arguments object.</param>
    /// <param name="propertyName">The DAP property name.</param>
    /// <param name="defaultValue">The value used when the property is absent.</param>
    /// <returns>The configured or default boolean value.</returns>
    internal static bool Get(
        JsonElement owner,
        string propertyName,
        bool defaultValue)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement value))
        {
            return defaultValue;
        }

        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new ArgumentException(
                $"The target '{propertyName}' value must be a boolean.");
        }

        return value.GetBoolean();
    }
}
