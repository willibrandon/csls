using System.Text;

namespace Csls.Debugger;

/// <summary>
/// Parses a bounded DAP logpoint message into literal and expression segments.
/// </summary>
internal sealed class DebugLogMessageTemplate
{
    private const int MaximumExpressionCount = 64;
    private const int MaximumExpressionLength = 4096;
    private const int MaximumMessageLength = 16 * 1024;

    private DebugLogMessageTemplate(IReadOnlyList<DebugLogMessageSegment> segments)
    {
        Segments = segments;
    }

    /// <summary>
    /// Gets the ordered literal and expression segments.
    /// </summary>
    internal IReadOnlyList<DebugLogMessageSegment> Segments { get; }

    /// <summary>
    /// Parses one logpoint message with doubled-brace escaping.
    /// </summary>
    /// <param name="message">The non-empty DAP logpoint message.</param>
    /// <returns>The bounded parsed template.</returns>
    internal static DebugLogMessageTemplate Parse(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > MaximumMessageLength)
        {
            throw new ArgumentException(
                $"A logpoint message cannot exceed {MaximumMessageLength} characters.",
                nameof(message));
        }

        var segments = new List<DebugLogMessageSegment>();
        var literal = new StringBuilder();
        int expressionCount = 0;
        for (int index = 0; index < message.Length;)
        {
            char current = message[index];
            if (current == '{')
            {
                if (index + 1 < message.Length && message[index + 1] == '{')
                {
                    _ = literal.Append('{');
                    index += 2;
                    continue;
                }

                AddLiteral(segments, literal);
                int end = FindExpressionEnd(message, index + 1);
                string expression = message[(index + 1)..end].Trim();
                if (expression.Length == 0)
                {
                    throw new ArgumentException(
                        "A logpoint interpolation cannot contain an empty expression.",
                        nameof(message));
                }

                if (expression.Length > MaximumExpressionLength)
                {
                    throw new ArgumentException(
                        $"A logpoint expression cannot exceed {MaximumExpressionLength} characters.",
                        nameof(message));
                }

                if (++expressionCount > MaximumExpressionCount)
                {
                    throw new ArgumentException(
                        $"A logpoint message cannot contain more than " +
                        $"{MaximumExpressionCount} expressions.",
                        nameof(message));
                }

                segments.Add(new DebugLogMessageSegment(expression, IsExpression: true));
                index = end + 1;
                continue;
            }

            if (current == '}')
            {
                if (index + 1 < message.Length && message[index + 1] == '}')
                {
                    _ = literal.Append('}');
                    index += 2;
                    continue;
                }

                throw new ArgumentException(
                    "A logpoint message contains an unmatched closing brace.",
                    nameof(message));
            }

            _ = literal.Append(current);
            index++;
        }

        AddLiteral(segments, literal);
        return new DebugLogMessageTemplate(segments);
    }

    private static void AddLiteral(
        List<DebugLogMessageSegment> segments,
        StringBuilder literal)
    {
        if (literal.Length == 0)
        {
            return;
        }

        segments.Add(new DebugLogMessageSegment(literal.ToString(), IsExpression: false));
        _ = literal.Clear();
    }

    private static int FindExpressionEnd(string message, int start)
    {
        char quote = '\0';
        bool escaped = false;
        for (int index = start; index < message.Length; index++)
        {
            char current = message[index];
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == quote)
                {
                    if (quote == '"' && index + 1 < message.Length &&
                        message[index + 1] == '"')
                    {
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (current is '\'' or '"')
            {
                quote = current;
            }
            else if (current == '}')
            {
                return index;
            }
            else if (current == '{')
            {
                throw new ArgumentException(
                    "A logpoint interpolation contains an unsupported nested opening brace.",
                    nameof(message));
            }
        }

        throw new ArgumentException(
            "A logpoint message contains an unmatched opening brace.",
            nameof(message));
    }
}
