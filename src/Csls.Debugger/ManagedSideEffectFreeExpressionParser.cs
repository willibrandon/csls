using System.Globalization;

namespace Csls.Debugger;

/// <summary>
/// Parses the non-executing expression subset understood by the runtime binder.
/// </summary>
internal static class ManagedSideEffectFreeExpressionParser
{
    private const int CurrentPlanVersion = 1;

    /// <summary>
    /// Parses an identifier-rooted field and array-access expression.
    /// </summary>
    /// <param name="expression">The source expression to parse.</param>
    /// <returns>The validated versioned evaluation plan.</returns>
    internal static ManagedExpressionPlan Parse(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ReadOnlySpan<char> source = expression.AsSpan().Trim();
        int position = 0;
        string root = ReadIdentifier(source, ref position);
        var segments = new List<ManagedExpressionSegment>();
        while (true)
        {
            SkipWhitespace(source, ref position);
            if (position == source.Length)
            {
                return new ManagedExpressionPlan(CurrentPlanVersion, root, segments);
            }

            if (source[position] == '.')
            {
                position++;
                SkipWhitespace(source, ref position);
                segments.Add(new ManagedExpressionSegment(
                    ManagedExpressionSegmentKind.Member,
                    ReadIdentifier(source, ref position)));
                continue;
            }

            if (source[position] == '[')
            {
                segments.Add(new ManagedExpressionSegment(
                    ManagedExpressionSegmentKind.ArrayIndex,
                    ReadArrayIndex(source, ref position)));
                continue;
            }

            throw Unsupported(expression);
        }
    }

    private static string ReadIdentifier(ReadOnlySpan<char> source, ref int position)
    {
        if (position < source.Length && source[position] == '@')
        {
            position++;
        }

        int start = position;
        if (position >= source.Length || !IsIdentifierStart(source[position]))
        {
            throw Unsupported(source.ToString());
        }

        position++;
        while (position < source.Length && IsIdentifierPart(source[position]))
        {
            position++;
        }

        return source[start..position].ToString();
    }

    private static string ReadArrayIndex(ReadOnlySpan<char> source, ref int position)
    {
        position++;
        var indexes = new List<int>();
        while (true)
        {
            SkipWhitespace(source, ref position);
            int start = position;
            if (position < source.Length && source[position] == '-')
            {
                position++;
            }

            int digitStart = position;
            while (position < source.Length && char.IsAsciiDigit(source[position]))
            {
                position++;
            }

            if (digitStart == position || !int.TryParse(
                source[start..position],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out int index))
            {
                throw Unsupported(source.ToString());
            }

            indexes.Add(index);
            SkipWhitespace(source, ref position);
            if (position < source.Length && source[position] == ',')
            {
                position++;
                continue;
            }

            if (position >= source.Length || source[position] != ']')
            {
                throw Unsupported(source.ToString());
            }

            position++;
            return $"[{string.Join(',', indexes)}]";
        }
    }

    private static void SkipWhitespace(ReadOnlySpan<char> source, ref int position)
    {
        while (position < source.Length && char.IsWhiteSpace(source[position]))
        {
            position++;
        }
    }

    private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);

    private static bool IsIdentifierPart(char value) =>
        value == '_' || char.IsLetterOrDigit(value);

    private static ArgumentException Unsupported(string expression) => new(
        $"The expression '{expression}' is outside the side-effect-free evaluator. " +
        "Use a local, argument, this, instance-field chain, or array index.");
}
