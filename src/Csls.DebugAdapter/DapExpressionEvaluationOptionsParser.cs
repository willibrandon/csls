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

        var result = new DebugExpressionEvaluationOptions();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty option in options.EnumerateObject())
        {
            if (option.Name is not ("showRawValues" or "allowImplicitFuncEval"))
            {
                throw new ArgumentException($"Unknown expressionEvaluationOptions member '{option.Name}'.");
            }

            if (!seen.Add(option.Name) || option.Value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                throw new ArgumentException($"expressionEvaluationOptions.{option.Name} must be a single boolean value.");
            }

            result = option.Name == "showRawValues"
                ? result with { ShowRawValues = option.Value.GetBoolean() }
                : result with { AllowImplicitFuncEval = option.Value.GetBoolean() };
        }

        return result;
    }
}
