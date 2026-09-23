using Csls.Debugger.Contracts;
using System.Globalization;

namespace Csls.Debugger;

/// <summary>
/// Parses the portable non-executing expression subset understood by the runtime binder.
/// </summary>
internal static class ManagedSideEffectFreeExpressionParser
{
    /// <summary>
    /// Parses an identifier-rooted field and array-access expression.
    /// </summary>
    /// <param name="expression">The source expression to parse.</param>
    /// <param name="language">The source language represented by the plan.</param>
    /// <returns>The validated versioned evaluation plan.</returns>
    internal static DebugExpressionPlan Parse(
        string expression,
        DebugExpressionLanguage language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ReadOnlySpan<char> source = expression.AsSpan().Trim();
        int position = 0;
        string root = ReadIdentifier(source, ref position);
        DebugExpressionNode current = string.Equals(root, "Me", StringComparison.Ordinal)
            ? Node(DebugExpressionNodeKind.This)
            : Node(DebugExpressionNodeKind.Identifier, root);
        while (true)
        {
            SkipWhitespace(source, ref position);
            if (position == source.Length)
            {
                return new DebugExpressionPlan(
                    DebuggerEvaluatorProtocol.CurrentPlanVersion,
                    language,
                    current);
            }

            if (source[position] == '.')
            {
                position++;
                SkipWhitespace(source, ref position);
                if (position < source.Length && source[position] == '[')
                {
                    current = ReadArrayAccess(source, ref position, current);
                    continue;
                }

                current = Node(
                    DebugExpressionNodeKind.MemberAccess,
                    ReadIdentifier(source, ref position),
                    current);
                continue;
            }

            if (source[position] == '[')
            {
                current = ReadArrayAccess(source, ref position, current);
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

    private static DebugExpressionNode ReadArrayAccess(
        ReadOnlySpan<char> source,
        ref int position,
        DebugExpressionNode receiver)
    {
        position++;
        var children = new List<DebugExpressionNode> { receiver };
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

            children.Add(new DebugExpressionNode(
                DebugExpressionNodeKind.Literal,
                DebugExpressionOperator.None,
                index.ToString(CultureInfo.InvariantCulture),
                "int",
                []));
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
            return new DebugExpressionNode(
                DebugExpressionNodeKind.ElementAccess,
                DebugExpressionOperator.None,
                Text: null,
                TypeName: null,
                children);
        }
    }

    private static DebugExpressionNode Node(
        DebugExpressionNodeKind kind,
        string? text = null,
        params DebugExpressionNode[] children) => new(
            kind,
            DebugExpressionOperator.None,
            text,
            TypeName: null,
            children);

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
        $"The expression '{expression}' is outside the portable side-effect-free evaluator. " +
        "Use a local, argument, instance-field chain, or array index.");
}
