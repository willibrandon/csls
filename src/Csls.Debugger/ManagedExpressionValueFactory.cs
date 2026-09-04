using Csls.Debugger.Contracts;
using System.Globalization;
using System.Text;

namespace Csls.Debugger;

/// <summary>
/// Decodes runtime primitives and creates debugger displays for computed values.
/// </summary>
internal static class ManagedExpressionValueFactory
{
    /// <summary>
    /// Wraps one runtime variable and decodes its scalar representation when possible.
    /// </summary>
    /// <param name="variable">The runtime-backed debugger variable.</param>
    /// <param name="runtimeValueReference">The internal retained identity independent of expansion handles.</param>
    /// <param name="runtimeValue">The semantic value and type before presentation transforms.</param>
    /// <returns>The expression value.</returns>
    internal static ManagedExpressionValue FromVariable(
        DebugVariableInfo variable,
        int runtimeValueReference,
        ManagedValueDisplay runtimeValue)
    {
        ArgumentNullException.ThrowIfNull(variable);
        bool hasScalar = TryParse(runtimeValue.Type, runtimeValue.Value, out object? scalar);
        return new ManagedExpressionValue(variable, scalar, hasScalar, runtimeValue.Type, runtimeValueReference);
    }

    /// <summary>
    /// Creates one compiler-decoded literal value.
    /// </summary>
    /// <param name="node">The validated literal node.</param>
    /// <returns>The computed literal value.</returns>
    internal static ManagedExpressionValue FromLiteral(DebugExpressionNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.TypeName is null)
        {
            if (node.Text is not null)
            {
                throw new InvalidDataException("A null literal cannot carry text.");
            }

            return FromScalar(value: null, "object");
        }

        if (string.Equals(node.TypeName, "string", StringComparison.Ordinal))
        {
            return FromScalar(node.Text ?? string.Empty, node.TypeName);
        }

        if (string.Equals(node.TypeName, "char", StringComparison.Ordinal))
        {
            if (node.Text is not { Length: 1 })
            {
                throw new InvalidDataException("A character literal must contain one character.");
            }

            return FromScalar(node.Text[0], node.TypeName);
        }

        if (!TryParse(node.TypeName, node.Text ?? string.Empty, out object? value))
        {
            throw new InvalidDataException(
                $"Literal type '{node.TypeName}' is not supported by safe evaluation.");
        }

        return FromScalar(value, node.TypeName);
    }

    /// <summary>
    /// Creates one non-expandable debugger value from an evaluated primitive.
    /// </summary>
    /// <param name="value">The computed CLR primitive or null.</param>
    /// <param name="typeName">The debugger-facing primitive type name.</param>
    /// <returns>The computed expression value.</returns>
    internal static ManagedExpressionValue FromScalar(object? value, string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        string display = value switch
        {
            null => "null",
            bool boolean => boolean ? "true" : "false",
            char character => QuoteCharacter(character),
            string characters => QuoteString(characters),
            float single => single.ToString("R", CultureInfo.InvariantCulture),
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(
                format: null,
                CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException(
                $"Computed value type {value.GetType().FullName} is not supported.")
        };
        return new ManagedExpressionValue(
            new DebugVariableInfo(
                "$result",
                display,
                typeName,
                VariablesReference: 0,
                MemoryReference: null,
                EvaluateName: null),
            value,
            HasScalar: true,
            Type: typeName);
    }

    /// <summary>
    /// Gets the scalar value required by a non-executing primitive operation.
    /// </summary>
    /// <param name="value">The expression operand.</param>
    /// <returns>The decoded primitive, which may be null.</returns>
    internal static object? RequireScalar(ManagedExpressionValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.HasScalar
            ? value.Scalar
            : throw new InvalidOperationException(
                $"Type '{value.Type}' cannot participate in safe primitive evaluation.");
    }

    /// <summary>
    /// Gets one Boolean operand or reports an exact type mismatch.
    /// </summary>
    /// <param name="value">The expression operand.</param>
    /// <returns>The decoded Boolean value.</returns>
    internal static bool RequireBoolean(ManagedExpressionValue value) =>
        RequireScalar(value) is bool boolean
            ? boolean
            : throw new InvalidOperationException(
                $"Type '{value.Type}' is not Boolean.");

    /// <summary>
    /// Gets one CLR array index from an evaluated integral operand.
    /// </summary>
    /// <param name="value">The evaluated index expression.</param>
    /// <returns>The checked signed array index.</returns>
    internal static int RequireArrayIndex(ManagedExpressionValue value)
    {
        object? scalar = RequireScalar(value);
        try
        {
            return scalar switch
            {
                sbyte number => number,
                byte number => number,
                short number => number,
                ushort number => number,
                int number => number,
                uint number => checked((int)number),
                long number => checked((int)number),
                ulong number => checked((int)number),
                _ => throw new InvalidOperationException(
                    $"Type '{value.Type}' is not a supported CLR array index.")
            };
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                $"Array index '{value.Display.Value}' is outside the Int32 range.",
                exception);
        }
    }

    private static bool TryParse(string type, string display, out object? value)
    {
        NumberStyles integer = NumberStyles.Integer;
        NumberStyles floating = NumberStyles.Float;
        IFormatProvider culture = CultureInfo.InvariantCulture;
        bool parsed = true;
        value = type switch
        {
            "bool" when bool.TryParse(display, out bool result) => result,
            "char" when TryUnquoteCharacter(display, out char result) => result,
            "string" when TryUnquoteString(display, out string? result) => result,
            "sbyte" when sbyte.TryParse(display, integer, culture, out sbyte result) => result,
            "byte" when byte.TryParse(display, integer, culture, out byte result) => result,
            "short" when short.TryParse(display, integer, culture, out short result) => result,
            "ushort" when ushort.TryParse(display, integer, culture, out ushort result) => result,
            "int" when int.TryParse(display, integer, culture, out int result) => result,
            "uint" when uint.TryParse(display, integer, culture, out uint result) => result,
            "long" when long.TryParse(display, integer, culture, out long result) => result,
            "ulong" when ulong.TryParse(display, integer, culture, out ulong result) => result,
            "nint" when long.TryParse(display, integer, culture, out long result) => result,
            "nuint" when ulong.TryParse(display, integer, culture, out ulong result) => result,
            "float" when float.TryParse(display, floating, culture, out float result) => result,
            "double" when double.TryParse(display, floating, culture, out double result) => result,
            "decimal" when decimal.TryParse(display, floating, culture, out decimal result) => result,
            _ when string.Equals(display, "null", StringComparison.Ordinal) => null,
            _ => Unparsed()
        };
        return parsed;

        object? Unparsed()
        {
            parsed = false;
            return null;
        }
    }

    private static string QuoteCharacter(char value) => value switch
    {
        '\\' => "'\\\\'",
        '\'' => "'\\\''",
        '\r' => "'\\r'",
        '\n' => "'\\n'",
        '\t' => "'\\t'",
        _ => $"'{value}'"
    };

    private static string QuoteString(string value)
    {
        var result = new StringBuilder(value.Length + 2);
        result.Append('"');
        foreach (char character in value)
        {
            result.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ => character.ToString()
            });
        }

        return result.Append('"').ToString();
    }

    private static bool TryUnquoteCharacter(string display, out char value)
    {
        value = default;
        if (display.Length < 3 || display[0] != '\'' || display[^1] != '\'')
        {
            return false;
        }

        string content = display[1..^1];
        if (!TryDecodeEscapes(content, out string decoded) || decoded.Length != 1)
        {
            return false;
        }

        value = decoded[0];
        return true;
    }

    private static bool TryUnquoteString(string display, out string? value)
    {
        value = null;
        if (display.Length < 2 || display[0] != '"' || display[^1] != '"')
        {
            return false;
        }

        if (!TryDecodeEscapes(display[1..^1], out string decoded))
        {
            return false;
        }

        value = decoded;
        return true;
    }

    private static bool TryDecodeEscapes(string content, out string value)
    {
        var result = new StringBuilder(content.Length);
        for (int index = 0; index < content.Length; index++)
        {
            char current = content[index];
            if (current != '\\')
            {
                result.Append(current);
                continue;
            }

            if (++index == content.Length)
            {
                value = string.Empty;
                return false;
            }

            result.Append(content[index] switch
            {
                '\\' => '\\',
                '\'' => '\'',
                '"' => '"',
                'r' => '\r',
                'n' => '\n',
                't' => '\t',
                _ => '\0'
            });
            if (result[^1] == '\0')
            {
                value = string.Empty;
                return false;
            }
        }

        value = result.ToString();
        return true;
    }
}
