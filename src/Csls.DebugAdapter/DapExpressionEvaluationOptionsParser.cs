using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Validates managed value presentation options shared by DAP launch and attach.
/// </summary>
internal static class DapExpressionEvaluationOptionsParser
{
    /// <summary>
    /// Reads the expression evaluation options object and its explicit Boolean presentation selection.
    /// </summary>
    /// <param name="arguments">The launch or attach argument object.</param>
    /// <returns>The immutable session presentation options.</returns>
    internal static DebugExpressionEvaluationOptions Parse(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("expressionEvaluationOptions", out JsonElement options))
        {
            return new();
        }

        if (options.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("expressionEvaluationOptions must be an object.");
        }

        bool showRawValues = false;
        bool seenRawValues = false;
        foreach (JsonProperty option in options.EnumerateObject())
        {
            if (option.Name != "showRawValues")
            {
                throw new ArgumentException($"Unknown expressionEvaluationOptions member '{option.Name}'.");
            }

            if (seenRawValues || option.Value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                throw new ArgumentException("expressionEvaluationOptions.showRawValues must be a single boolean value.");
            }

            seenRawValues = true;
            showRawValues = option.Value.GetBoolean();
        }

        return new()
        {
            ShowRawValues = showRawValues
        };
    }
}
