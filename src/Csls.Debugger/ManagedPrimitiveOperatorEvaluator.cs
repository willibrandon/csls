using Csls.Debugger.Contracts;
using System.Diagnostics;
using System.Globalization;

namespace Csls.Debugger;

/// <summary>
/// Applies non-executing operators to built-in CLR primitive values.
/// </summary>
internal static class ManagedPrimitiveOperatorEvaluator
{
    /// <summary>
    /// Applies one normalized unary operator without invoking target code.
    /// </summary>
    /// <param name="operation">The normalized unary operator.</param>
    /// <param name="operand">The evaluated primitive operand.</param>
    /// <returns>The computed primitive result.</returns>
    internal static ManagedExpressionValue EvaluateUnary(
        DebugExpressionOperator operation,
        ManagedExpressionValue operand)
    {
        object? value = ManagedExpressionValueFactory.RequireScalar(operand);
        if (operation == DebugExpressionOperator.LogicalNot)
        {
            return ManagedExpressionValueFactory.FromScalar(
                !ManagedExpressionValueFactory.RequireBoolean(operand),
                "bool");
        }

        ManagedNumericValue numeric = GetNumeric(value, operand.Display.Type);
        object result = operation switch
        {
            DebugExpressionOperator.UnaryPlus => numeric.Value,
            DebugExpressionOperator.Negate => Negate(numeric),
            DebugExpressionOperator.OnesComplement => OnesComplement(numeric),
            _ => throw Unsupported(operation)
        };
        ManagedNumericKind resultKind = operation == DebugExpressionOperator.Negate &&
            numeric.Kind == ManagedNumericKind.UInt32
                ? ManagedNumericKind.Int64
                : numeric.Kind;
        return ManagedExpressionValueFactory.FromScalar(result, TypeName(resultKind));
    }

    /// <summary>
    /// Applies one normalized binary operator without invoking target code.
    /// </summary>
    /// <param name="operation">The normalized binary operator.</param>
    /// <param name="left">The evaluated left operand.</param>
    /// <param name="right">The evaluated right operand.</param>
    /// <returns>The computed primitive result.</returns>
    internal static ManagedExpressionValue EvaluateBinary(
        DebugExpressionOperator operation,
        ManagedExpressionValue left,
        ManagedExpressionValue right)
    {
        object? leftValue = ManagedExpressionValueFactory.RequireScalar(left);
        object? rightValue = ManagedExpressionValueFactory.RequireScalar(right);
        if (operation is DebugExpressionOperator.Equal or DebugExpressionOperator.NotEqual)
        {
            bool equal = Equal(leftValue, rightValue, left.Display.Type, right.Display.Type);
            return ManagedExpressionValueFactory.FromScalar(
                operation == DebugExpressionOperator.Equal ? equal : !equal,
                "bool");
        }

        if (operation == DebugExpressionOperator.Add &&
            leftValue is string leftString && rightValue is string rightString)
        {
            return ManagedExpressionValueFactory.FromScalar(
                string.Concat(leftString, rightString),
                "string");
        }

        if (leftValue is bool leftBoolean && rightValue is bool rightBoolean)
        {
            bool result = operation switch
            {
                DebugExpressionOperator.BitwiseAnd => leftBoolean & rightBoolean,
                DebugExpressionOperator.BitwiseOr => leftBoolean | rightBoolean,
                DebugExpressionOperator.ExclusiveOr => leftBoolean ^ rightBoolean,
                _ => throw Unsupported(operation)
            };
            return ManagedExpressionValueFactory.FromScalar(result, "bool");
        }

        ManagedNumericValue leftNumeric = GetNumeric(leftValue, left.Display.Type);
        ManagedNumericValue rightNumeric = GetNumeric(rightValue, right.Display.Type);
        ManagedNumericKind kind = Promote(leftNumeric.Kind, rightNumeric.Kind);
        if (operation is DebugExpressionOperator.LessThan or
            DebugExpressionOperator.LessThanOrEqual or
            DebugExpressionOperator.GreaterThan or
            DebugExpressionOperator.GreaterThanOrEqual)
        {
            int comparison = Compare(leftNumeric, rightNumeric, kind);
            bool result = operation switch
            {
                DebugExpressionOperator.LessThan => comparison < 0,
                DebugExpressionOperator.LessThanOrEqual => comparison <= 0,
                DebugExpressionOperator.GreaterThan => comparison > 0,
                DebugExpressionOperator.GreaterThanOrEqual => comparison >= 0,
                _ => throw new UnreachableException()
            };
            return ManagedExpressionValueFactory.FromScalar(result, "bool");
        }

        object computed = ApplyNumeric(operation, leftNumeric, rightNumeric, kind);
        return ManagedExpressionValueFactory.FromScalar(computed, TypeName(kind));
    }

    private static bool Equal(
        object? left,
        object? right,
        string leftType,
        string rightType)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left is string leftString && right is string rightString)
        {
            return string.Equals(leftString, rightString, StringComparison.Ordinal);
        }

        if (left is bool leftBoolean && right is bool rightBoolean)
        {
            return leftBoolean == rightBoolean;
        }

        ManagedNumericValue leftNumeric = GetNumeric(left, leftType);
        ManagedNumericValue rightNumeric = GetNumeric(right, rightType);
        ManagedNumericKind kind = Promote(leftNumeric.Kind, rightNumeric.Kind);
        return Compare(leftNumeric, rightNumeric, kind) == 0;
    }

    private static object ApplyNumeric(
        DebugExpressionOperator operation,
        ManagedNumericValue left,
        ManagedNumericValue right,
        ManagedNumericKind kind) => kind switch
        {
            ManagedNumericKind.Int32 => ApplyInt32(
                operation,
                Convert.ToInt32(left.Value, CultureInfo.InvariantCulture),
                Convert.ToInt32(right.Value, CultureInfo.InvariantCulture)),
            ManagedNumericKind.UInt32 => ApplyUInt32(
                operation,
                Convert.ToUInt32(left.Value, CultureInfo.InvariantCulture),
                Convert.ToUInt32(right.Value, CultureInfo.InvariantCulture)),
            ManagedNumericKind.Int64 => ApplyInt64(
                operation,
                Convert.ToInt64(left.Value, CultureInfo.InvariantCulture),
                Convert.ToInt64(right.Value, CultureInfo.InvariantCulture)),
            ManagedNumericKind.UInt64 => ApplyUInt64(
                operation,
                Convert.ToUInt64(left.Value, CultureInfo.InvariantCulture),
                Convert.ToUInt64(right.Value, CultureInfo.InvariantCulture)),
            ManagedNumericKind.Single => ApplySingle(
                operation,
                Convert.ToSingle(left.Value, CultureInfo.InvariantCulture),
                Convert.ToSingle(right.Value, CultureInfo.InvariantCulture)),
            ManagedNumericKind.Double => ApplyDouble(
                operation,
                Convert.ToDouble(left.Value, CultureInfo.InvariantCulture),
                Convert.ToDouble(right.Value, CultureInfo.InvariantCulture)),
            ManagedNumericKind.Decimal => ApplyDecimal(
                operation,
                Convert.ToDecimal(left.Value, CultureInfo.InvariantCulture),
                Convert.ToDecimal(right.Value, CultureInfo.InvariantCulture)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static int ApplyInt32(DebugExpressionOperator operation, int left, int right) =>
        operation switch
        {
            DebugExpressionOperator.Add => unchecked(left + right),
            DebugExpressionOperator.Subtract => unchecked(left - right),
            DebugExpressionOperator.Multiply => unchecked(left * right),
            DebugExpressionOperator.Divide => left / right,
            DebugExpressionOperator.Remainder => left % right,
            DebugExpressionOperator.BitwiseAnd => left & right,
            DebugExpressionOperator.BitwiseOr => left | right,
            DebugExpressionOperator.ExclusiveOr => left ^ right,
            _ => throw Unsupported(operation)
        };

    private static uint ApplyUInt32(DebugExpressionOperator operation, uint left, uint right) =>
        operation switch
        {
            DebugExpressionOperator.Add => unchecked(left + right),
            DebugExpressionOperator.Subtract => unchecked(left - right),
            DebugExpressionOperator.Multiply => unchecked(left * right),
            DebugExpressionOperator.Divide => left / right,
            DebugExpressionOperator.Remainder => left % right,
            DebugExpressionOperator.BitwiseAnd => left & right,
            DebugExpressionOperator.BitwiseOr => left | right,
            DebugExpressionOperator.ExclusiveOr => left ^ right,
            _ => throw Unsupported(operation)
        };

    private static long ApplyInt64(DebugExpressionOperator operation, long left, long right) =>
        operation switch
        {
            DebugExpressionOperator.Add => unchecked(left + right),
            DebugExpressionOperator.Subtract => unchecked(left - right),
            DebugExpressionOperator.Multiply => unchecked(left * right),
            DebugExpressionOperator.Divide => left / right,
            DebugExpressionOperator.Remainder => left % right,
            DebugExpressionOperator.BitwiseAnd => left & right,
            DebugExpressionOperator.BitwiseOr => left | right,
            DebugExpressionOperator.ExclusiveOr => left ^ right,
            _ => throw Unsupported(operation)
        };

    private static ulong ApplyUInt64(DebugExpressionOperator operation, ulong left, ulong right) =>
        operation switch
        {
            DebugExpressionOperator.Add => unchecked(left + right),
            DebugExpressionOperator.Subtract => unchecked(left - right),
            DebugExpressionOperator.Multiply => unchecked(left * right),
            DebugExpressionOperator.Divide => left / right,
            DebugExpressionOperator.Remainder => left % right,
            DebugExpressionOperator.BitwiseAnd => left & right,
            DebugExpressionOperator.BitwiseOr => left | right,
            DebugExpressionOperator.ExclusiveOr => left ^ right,
            _ => throw Unsupported(operation)
        };

    private static float ApplySingle(DebugExpressionOperator operation, float left, float right) =>
        operation switch
        {
            DebugExpressionOperator.Add => left + right,
            DebugExpressionOperator.Subtract => left - right,
            DebugExpressionOperator.Multiply => left * right,
            DebugExpressionOperator.Divide => left / right,
            DebugExpressionOperator.Remainder => left % right,
            _ => throw Unsupported(operation)
        };

    private static double ApplyDouble(
        DebugExpressionOperator operation,
        double left,
        double right) => operation switch
        {
            DebugExpressionOperator.Add => left + right,
            DebugExpressionOperator.Subtract => left - right,
            DebugExpressionOperator.Multiply => left * right,
            DebugExpressionOperator.Divide => left / right,
            DebugExpressionOperator.Remainder => left % right,
            _ => throw Unsupported(operation)
        };

    private static decimal ApplyDecimal(
        DebugExpressionOperator operation,
        decimal left,
        decimal right) => operation switch
        {
            DebugExpressionOperator.Add => left + right,
            DebugExpressionOperator.Subtract => left - right,
            DebugExpressionOperator.Multiply => left * right,
            DebugExpressionOperator.Divide => left / right,
            DebugExpressionOperator.Remainder => left % right,
            _ => throw Unsupported(operation)
        };

    private static object Negate(ManagedNumericValue value) => value.Kind switch
    {
        ManagedNumericKind.Int32 => unchecked(-Convert.ToInt32(
            value.Value,
            CultureInfo.InvariantCulture)),
        ManagedNumericKind.UInt32 => -Convert.ToInt64(
            value.Value,
            CultureInfo.InvariantCulture),
        ManagedNumericKind.Int64 => unchecked(-Convert.ToInt64(
            value.Value,
            CultureInfo.InvariantCulture)),
        ManagedNumericKind.UInt64 => throw new InvalidOperationException(
            "Unary negation is not defined for UInt64."),
        ManagedNumericKind.Single => -Convert.ToSingle(
            value.Value,
            CultureInfo.InvariantCulture),
        ManagedNumericKind.Double => -Convert.ToDouble(
            value.Value,
            CultureInfo.InvariantCulture),
        ManagedNumericKind.Decimal => -Convert.ToDecimal(
            value.Value,
            CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static object OnesComplement(ManagedNumericValue value) => value.Kind switch
    {
        ManagedNumericKind.Int32 => ~Convert.ToInt32(value.Value, CultureInfo.InvariantCulture),
        ManagedNumericKind.UInt32 => ~Convert.ToUInt32(value.Value, CultureInfo.InvariantCulture),
        ManagedNumericKind.Int64 => ~Convert.ToInt64(value.Value, CultureInfo.InvariantCulture),
        ManagedNumericKind.UInt64 => ~Convert.ToUInt64(value.Value, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException(
            $"Ones-complement is not defined for {TypeName(value.Kind)}.")
    };

    private static int Compare(
        ManagedNumericValue left,
        ManagedNumericValue right,
        ManagedNumericKind kind) => kind switch
        {
            ManagedNumericKind.Int32 => Convert.ToInt32(
                left.Value,
                CultureInfo.InvariantCulture).CompareTo(Convert.ToInt32(
                    right.Value,
                    CultureInfo.InvariantCulture)),
            ManagedNumericKind.UInt32 => Convert.ToUInt32(
                left.Value,
                CultureInfo.InvariantCulture).CompareTo(Convert.ToUInt32(
                    right.Value,
                    CultureInfo.InvariantCulture)),
            ManagedNumericKind.Int64 => Convert.ToInt64(
                left.Value,
                CultureInfo.InvariantCulture).CompareTo(Convert.ToInt64(
                    right.Value,
                    CultureInfo.InvariantCulture)),
            ManagedNumericKind.UInt64 => Convert.ToUInt64(
                left.Value,
                CultureInfo.InvariantCulture).CompareTo(Convert.ToUInt64(
                    right.Value,
                    CultureInfo.InvariantCulture)),
            ManagedNumericKind.Single => Convert.ToSingle(
                left.Value,
                CultureInfo.InvariantCulture).CompareTo(Convert.ToSingle(
                    right.Value,
                    CultureInfo.InvariantCulture)),
            ManagedNumericKind.Double => Convert.ToDouble(
                left.Value,
                CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(
                    right.Value,
                    CultureInfo.InvariantCulture)),
            ManagedNumericKind.Decimal => Convert.ToDecimal(
                left.Value,
                CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(
                    right.Value,
                    CultureInfo.InvariantCulture)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static ManagedNumericKind Promote(
        ManagedNumericKind left,
        ManagedNumericKind right)
    {
        if (left == ManagedNumericKind.Decimal || right == ManagedNumericKind.Decimal)
        {
            if (left is ManagedNumericKind.Single or ManagedNumericKind.Double ||
                right is ManagedNumericKind.Single or ManagedNumericKind.Double)
            {
                throw new InvalidOperationException(
                    "Decimal and binary floating-point operands cannot be mixed safely.");
            }

            return ManagedNumericKind.Decimal;
        }

        if (left == ManagedNumericKind.Double || right == ManagedNumericKind.Double)
        {
            return ManagedNumericKind.Double;
        }

        if (left == ManagedNumericKind.Single || right == ManagedNumericKind.Single)
        {
            return ManagedNumericKind.Single;
        }

        if (left == ManagedNumericKind.UInt64 || right == ManagedNumericKind.UInt64)
        {
            if (left is ManagedNumericKind.Int32 or ManagedNumericKind.Int64 ||
                right is ManagedNumericKind.Int32 or ManagedNumericKind.Int64)
            {
                throw new InvalidOperationException(
                    "UInt64 and signed integral operands cannot be mixed safely.");
            }

            return ManagedNumericKind.UInt64;
        }

        if (left == ManagedNumericKind.Int64 || right == ManagedNumericKind.Int64)
        {
            return ManagedNumericKind.Int64;
        }

        if (left == ManagedNumericKind.UInt32 || right == ManagedNumericKind.UInt32)
        {
            return left == ManagedNumericKind.Int32 || right == ManagedNumericKind.Int32
                ? ManagedNumericKind.Int64
                : ManagedNumericKind.UInt32;
        }

        return ManagedNumericKind.Int32;
    }

    private static ManagedNumericValue GetNumeric(object? value, string displayType) => value switch
    {
        sbyte number => new ManagedNumericValue(ManagedNumericKind.Int32, (int)number),
        byte number => new ManagedNumericValue(ManagedNumericKind.Int32, (int)number),
        short number => new ManagedNumericValue(ManagedNumericKind.Int32, (int)number),
        ushort number => new ManagedNumericValue(ManagedNumericKind.Int32, (int)number),
        char character => new ManagedNumericValue(ManagedNumericKind.Int32, (int)character),
        int number => new ManagedNumericValue(ManagedNumericKind.Int32, number),
        uint number => new ManagedNumericValue(ManagedNumericKind.UInt32, number),
        long number => new ManagedNumericValue(ManagedNumericKind.Int64, number),
        ulong number => new ManagedNumericValue(ManagedNumericKind.UInt64, number),
        float number => new ManagedNumericValue(ManagedNumericKind.Single, number),
        double number => new ManagedNumericValue(ManagedNumericKind.Double, number),
        decimal number => new ManagedNumericValue(ManagedNumericKind.Decimal, number),
        _ => throw new InvalidOperationException(
            $"Type '{displayType}' is not a built-in numeric type.")
    };

    private static string TypeName(ManagedNumericKind kind) => kind switch
    {
        ManagedNumericKind.Int32 => "int",
        ManagedNumericKind.UInt32 => "uint",
        ManagedNumericKind.Int64 => "long",
        ManagedNumericKind.UInt64 => "ulong",
        ManagedNumericKind.Single => "float",
        ManagedNumericKind.Double => "double",
        ManagedNumericKind.Decimal => "decimal",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static InvalidOperationException Unsupported(
        DebugExpressionOperator operation) => new(
        $"Operator '{operation}' is unavailable for the selected safe primitive operands.");
}
