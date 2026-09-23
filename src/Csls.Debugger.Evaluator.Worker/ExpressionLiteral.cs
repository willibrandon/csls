using Csls.Debugger.Contracts;
using System.Globalization;

namespace Csls.Debugger.Evaluator.Worker;

/// <summary>
/// Converts compiler-decoded literal values to transport-safe expression nodes.
/// </summary>
internal static class ExpressionLiteral
{
    /// <summary>
    /// Creates one literal node from a compiler token value.
    /// </summary>
    /// <param name="value">The decoded compiler token value.</param>
    /// <returns>The language-neutral literal operation.</returns>
    internal static DebugExpressionNode Create(object? value)
    {
        string? text = value switch
        {
            null => null,
            bool boolean => boolean ? "true" : "false",
            char character => character.ToString(),
            string characters => characters,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException(
                $"Literal type {value.GetType().FullName} is not supported.")
        };
        string? typeName = value switch
        {
            null => null,
            bool => "bool",
            char => "char",
            string => "string",
            sbyte => "sbyte",
            byte => "byte",
            short => "short",
            ushort => "ushort",
            int => "int",
            uint => "uint",
            long => "long",
            ulong => "ulong",
            float => "float",
            double => "double",
            decimal => "decimal",
            _ => throw new NotSupportedException(
                $"Literal type {value.GetType().FullName} is not supported.")
        };
        return new DebugExpressionNode(
            DebugExpressionNodeKind.Literal,
            DebugExpressionOperator.None,
            text,
            typeName,
            []);
    }
}
