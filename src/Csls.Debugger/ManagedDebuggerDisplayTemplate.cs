using System.Text;

namespace Csls.Debugger;

/// <summary>
/// Renders bounded debugger-display templates from side-effect-free runtime values.
/// </summary>
internal static class ManagedDebuggerDisplayTemplate
{
    private const int MaximumExpressionCount = 64;
    private const int MaximumOutputLength = 1024 * 1024;
    private const int MaximumTemplateLength = 16 * 1024;

    /// <summary>
    /// Renders one template with field expressions supplied by the caller.
    /// </summary>
    /// <param name="template">The metadata template to render.</param>
    /// <param name="resolve">The side-effect-free field-expression resolver.</param>
    /// <param name="result">Receives the rendered text when the template is valid.</param>
    /// <returns>True when every template segment was rendered safely.</returns>
    internal static bool TryRender(
        string template,
        Func<string, ManagedValueDisplay?> resolve,
        out string result)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(resolve);
        result = string.Empty;
        if (template.Length > MaximumTemplateLength)
        {
            return false;
        }

        var builder = new StringBuilder(template.Length);
        int expressionCount = 0;
        for (int index = 0; index < template.Length; index++)
        {
            char character = template[index];
            if (character == '{')
            {
                if (index + 1 < template.Length && template[index + 1] == '{')
                {
                    builder.Append('{');
                    index++;
                    continue;
                }

                int close = template.IndexOf('}', index + 1);
                if (close < 0 || template.AsSpan(index + 1, close - index - 1).Contains('{'))
                {
                    return false;
                }

                if (++expressionCount > MaximumExpressionCount ||
                    !TryAppendExpression(
                        builder,
                        template.AsSpan(index + 1, close - index - 1),
                        resolve))
                {
                    return false;
                }

                index = close;
            }
            else if (character == '}')
            {
                if (index + 1 >= template.Length || template[index + 1] != '}')
                {
                    return false;
                }

                builder.Append('}');
                index++;
            }
            else
            {
                builder.Append(character);
            }

            if (builder.Length > MaximumOutputLength)
            {
                return false;
            }
        }

        result = builder.ToString();
        return true;
    }

    private static bool TryAppendExpression(
        StringBuilder builder,
        ReadOnlySpan<char> expressionAndFormat,
        Func<string, ManagedValueDisplay?> resolve)
    {
        ReadOnlySpan<char> expression = expressionAndFormat;
        bool suppressQuotes = false;
        int separator = expressionAndFormat.LastIndexOf(',');
        if (separator >= 0)
        {
            expression = expressionAndFormat[..separator];
            ReadOnlySpan<char> format = expressionAndFormat[(separator + 1)..].Trim();
            if (!format.Equals("nq", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            suppressQuotes = true;
        }

        string expressionText = expression.Trim().ToString();
        if (expressionText.Length == 0 || resolve(expressionText) is not { } value)
        {
            return false;
        }

        string text = suppressQuotes &&
            string.Equals(value.Type, "string", StringComparison.Ordinal) &&
            value.Value.Length >= 2 &&
            value.Value[0] == '"' &&
            value.Value[^1] == '"'
                ? value.Value[1..^1]
                : value.Value;
        if (text.Length > MaximumOutputLength - builder.Length)
        {
            return false;
        }

        builder.Append(text);
        return true;
    }
}
