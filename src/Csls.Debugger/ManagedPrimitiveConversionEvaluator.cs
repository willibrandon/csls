using Csls.Debugger.Contracts;
using System.Numerics;

namespace Csls.Debugger;

/// <summary>
/// Applies compiler-shaped built-in CLR primitive conversions without target execution.
/// </summary>
internal static class ManagedPrimitiveConversionEvaluator
{
    /// <summary>
    /// Applies an explicit source-language primitive conversion.
    /// </summary>
    /// <param name="value">The already evaluated primitive operand.</param>
    /// <param name="destinationType">The compiler-provided destination type spelling.</param>
    /// <param name="language">The expression language controlling conversion behavior.</param>
    /// <returns>The converted primitive value.</returns>
    internal static ManagedExpressionValue EvaluateExplicit(
        ManagedExpressionValue value,
        string destinationType,
        DebugExpressionLanguage language)
    {
        ArgumentNullException.ThrowIfNull(value);
        string target = NormalizeTypeName(destinationType, language);
        string source = NormalizeTypeName(value.Type, DebugExpressionLanguage.CSharp);
        if (string.Equals(source, target, StringComparison.Ordinal))
        {
            return value;
        }

        object? scalar = ManagedExpressionValueFactory.RequireScalar(value);
        if (scalar is null)
        {
            if (target is "object" or "string")
            {
                return ManagedExpressionValueFactory.FromScalar(value: null, target);
            }

            throw CannotConvert(source, target);
        }

        if (!IsNumeric(source) || !IsNumeric(target))
        {
            throw CannotConvert(source, target);
        }

        return ConvertNumeric(
            scalar,
            target,
            checkedConversion: language == DebugExpressionLanguage.VisualBasic);
    }

    /// <summary>
    /// Applies a non-executing assignment conversion accepted by the source language.
    /// </summary>
    /// <param name="value">The already evaluated assignment value.</param>
    /// <param name="destinationType">The exact runtime destination type.</param>
    /// <param name="language">The selected frame language.</param>
    /// <param name="sourceIsContextualLiteral">Whether the source is one literal expression.</param>
    /// <returns>The value converted to the runtime destination type.</returns>
    internal static ManagedExpressionValue ConvertForAssignment(
        ManagedExpressionValue value,
        string destinationType,
        DebugExpressionLanguage language,
        bool sourceIsContextualLiteral)
    {
        ArgumentNullException.ThrowIfNull(value);
        string target = NormalizeTypeName(destinationType, DebugExpressionLanguage.CSharp);
        string source = NormalizeTypeName(value.Type, DebugExpressionLanguage.CSharp);
        if (value.DeclaredType is { IsReference: true } declared)
        {
            throw new InvalidOperationException(
                $"Assignment from '{declared.DisplayName}' to '{destinationType}' requires an explicit unboxing conversion.");
        }

        if (string.Equals(source, target, StringComparison.Ordinal))
        {
            return value;
        }

        object? scalar = ManagedExpressionValueFactory.RequireScalar(value);
        bool contextualIntegralLiteral = sourceIsContextualLiteral && IsIntegral(source);
        if (scalar is null || !IsNumeric(source) || !IsNumeric(target) ||
            !contextualIntegralLiteral && !IsImplicitNumericConversion(source, target, language))
        {
            throw new InvalidOperationException(
                $"Assignment from '{value.Type}' to '{destinationType}' requires " +
                "an explicit supported conversion.");
        }

        try
        {
            return ConvertNumeric(scalar, target, checkedConversion: true);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                $"Value '{value.Display.Value}' is outside the range of '{destinationType}'.",
                exception);
        }
    }

    /// <summary>
    /// Tests whether a loaded primitive argument can widen to a callable parameter.
    /// </summary>
    internal static bool IsImplicitInvocationConversion(
        ManagedBoundType source,
        ManagedBoundType target,
        DebugExpressionLanguage language)
    {
        if (!AreLoadedNumericTypes(source, target))
        {
            return false;
        }

        string? sourceName = TryNormalizeTypeName(source.Name, DebugExpressionLanguage.CSharp);
        string? targetName = TryNormalizeTypeName(target.Name, DebugExpressionLanguage.CSharp);
        return sourceName is not null && targetName is not null &&
            IsImplicitNumericConversion(sourceName, targetName, language);
    }

    /// <summary>
    /// Tests whether loaded numeric types have a standard explicit conversion around a user-defined operator.
    /// </summary>
    internal static bool IsStandardExplicitUserDefinedConversion(
        ManagedBoundType source,
        ManagedBoundType target,
        DebugExpressionLanguage language)
    {
        if (!AreLoadedNumericTypes(source, target))
        {
            return false;
        }

        string? sourceName = TryNormalizeTypeName(source.Name, DebugExpressionLanguage.CSharp);
        string? targetName = TryNormalizeTypeName(target.Name, DebugExpressionLanguage.CSharp);
        return sourceName is not null && targetName is not null &&
            (string.Equals(sourceName, targetName, StringComparison.Ordinal) ||
             IsImplicitNumericConversion(sourceName, targetName, language) ||
             IsImplicitNumericConversion(targetName, sourceName, language));
    }

    /// <summary>
    /// Materializes a standard explicit numeric conversion around a user-defined operator.
    /// </summary>
    internal static ManagedExpressionValue ConvertStandardExplicitUserDefinedConversion(
        ManagedExpressionValue value,
        ManagedBoundType source,
        ManagedBoundType target,
        DebugExpressionLanguage language)
    {
        if (!IsStandardExplicitUserDefinedConversion(source, target, language))
        {
            throw new InvalidOperationException(
                $"Type '{source.DisplayName}' has no standard explicit numeric conversion to " +
                $"'{target.DisplayName}'.");
        }

        object? scalar = ManagedExpressionValueFactory.RequireScalar(value);
        if (scalar is null)
        {
            throw new InvalidOperationException(
                "A null value cannot participate in a standard explicit numeric conversion.");
        }

        string targetName = TryNormalizeTypeName(target.Name, DebugExpressionLanguage.CSharp)
            ?? throw new InvalidOperationException(
                $"Unsupported numeric conversion target '{target.DisplayName}'.");
        return ConvertNumeric(
            scalar,
            targetName,
            checkedConversion: language == DebugExpressionLanguage.VisualBasic);
    }

    /// <summary>
    /// Tests an integral constant expression against C#'s range-limited invocation conversions.
    /// </summary>
    internal static bool IsImplicitConstantInvocationConversion(
        ManagedExpressionValue value,
        ManagedBoundType source,
        ManagedBoundType target,
        DebugExpressionLanguage language)
    {
        if (language != DebugExpressionLanguage.CSharp ||
            !AreLoadedNumericTypes(source, target))
        {
            return false;
        }

        string? sourceName = TryNormalizeTypeName(source.Name, language);
        string? targetName = TryNormalizeTypeName(target.Name, language);
        bool permitted = sourceName switch
        {
            "int" when value is { HasScalar: true, Scalar: int } =>
                targetName is "sbyte" or "byte" or "short" or "ushort" or "uint" or "nuint" or "ulong",
            "long" when value is { HasScalar: true, Scalar: long } => targetName == "ulong",
            _ => false
        };
        if (!permitted)
        {
            return false;
        }

        try
        {
            _ = ConvertNumeric(value.Scalar!, targetName!, checkedConversion: true);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// Materializes an applicable integral constant in its selected parameter type.
    /// </summary>
    internal static ManagedExpressionValue ConvertInvocationConstant(
        ManagedExpressionValue value,
        ManagedBoundType source,
        ManagedBoundType target,
        DebugExpressionLanguage language)
    {
        if (!IsImplicitConstantInvocationConversion(value, source, target, language))
        {
            throw new InvalidOperationException(
                $"Constant '{value.Display.Value}' cannot convert to '{target.DisplayName}'.");
        }

        string targetName = TryNormalizeTypeName(target.Name, language)
            ?? throw new InvalidOperationException($"Unsupported numeric parameter '{target.DisplayName}'.");
        return ConvertNumeric(value.Scalar!, targetName, checkedConversion: true);
    }

    /// <summary>
    /// Applies C#'s signed-integral better-target rule to two loaded numeric parameters.
    /// </summary>
    internal static bool IsPreferredSignedInvocationTarget(
        ManagedBoundType preferred,
        ManagedBoundType alternative,
        DebugExpressionLanguage language)
    {
        if (language != DebugExpressionLanguage.CSharp ||
            !AreLoadedNumericTypes(preferred, alternative))
        {
            return false;
        }

        string? signed = TryNormalizeTypeName(preferred.Name, language);
        string? unsigned = TryNormalizeTypeName(alternative.Name, language);
        return signed switch
        {
            "sbyte" => unsigned is "byte" or "ushort" or "uint" or "nuint" or "ulong",
            "short" => unsigned is "ushort" or "uint" or "nuint" or "ulong",
            "int" => unsigned is "uint" or "nuint" or "ulong",
            "nint" => unsigned is "nuint" or "ulong",
            "long" => unsigned is "nuint" or "ulong",
            _ => false
        };
    }

    /// <summary>
    /// Materializes a numeric argument in the selected callable parameter's CLR type.
    /// </summary>
    internal static ManagedExpressionValue ConvertForInvocation(
        ManagedExpressionValue value,
        ManagedBoundType source,
        ManagedBoundType target,
        DebugExpressionLanguage language)
    {
        if (!IsImplicitInvocationConversion(source, target, language))
        {
            throw new InvalidOperationException(
                $"Argument type '{source.DisplayName}' cannot widen to '{target.DisplayName}'.");
        }

        object? scalar = ManagedExpressionValueFactory.RequireScalar(value);
        if (scalar is null)
        {
            throw new InvalidOperationException("A null value cannot be a numeric invocation argument.");
        }

        string targetName = TryNormalizeTypeName(target.Name, DebugExpressionLanguage.CSharp)
            ?? throw new InvalidOperationException($"Unsupported numeric parameter '{target.DisplayName}'.");
        return ConvertNumeric(scalar, targetName, checkedConversion: true);
    }

    private static ManagedExpressionValue ConvertNumeric(
        object value,
        string target,
        bool checkedConversion)
    {
        object converted = target switch
        {
            "sbyte" => ConvertNumber<sbyte>(value, checkedConversion),
            "byte" => ConvertNumber<byte>(value, checkedConversion),
            "short" => ConvertNumber<short>(value, checkedConversion),
            "ushort" => ConvertNumber<ushort>(value, checkedConversion),
            "int" => ConvertNumber<int>(value, checkedConversion),
            "uint" => ConvertNumber<uint>(value, checkedConversion),
            "long" => ConvertNumber<long>(value, checkedConversion),
            "ulong" => ConvertNumber<ulong>(value, checkedConversion),
            "nint" => checked((long)ConvertNumber<nint>(value, checkedConversion)),
            "nuint" => checked((ulong)ConvertNumber<nuint>(value, checkedConversion)),
            "char" => (char)ConvertNumber<ushort>(value, checkedConversion),
            "float" => ConvertNumber<float>(value, checkedConversion),
            "double" => ConvertNumber<double>(value, checkedConversion),
            "decimal" => ConvertNumber<decimal>(value, checkedConversion),
            _ => throw new InvalidOperationException(
                $"Type '{target}' is not a built-in numeric type.")
        };
        return ManagedExpressionValueFactory.FromScalar(converted, target);
    }

    private static TTarget ConvertNumber<TTarget>(object value, bool checkedConversion)
        where TTarget : INumberBase<TTarget> => value switch
        {
            char number => Create<TTarget, ushort>(number, checkedConversion),
            sbyte number => Create<TTarget, sbyte>(number, checkedConversion),
            byte number => Create<TTarget, byte>(number, checkedConversion),
            short number => Create<TTarget, short>(number, checkedConversion),
            ushort number => Create<TTarget, ushort>(number, checkedConversion),
            int number => Create<TTarget, int>(number, checkedConversion),
            uint number => Create<TTarget, uint>(number, checkedConversion),
            long number => Create<TTarget, long>(number, checkedConversion),
            ulong number => Create<TTarget, ulong>(number, checkedConversion),
            nint number => Create<TTarget, nint>(number, checkedConversion),
            nuint number => Create<TTarget, nuint>(number, checkedConversion),
            float number => Create<TTarget, float>(number, checkedConversion),
            double number => Create<TTarget, double>(number, checkedConversion),
            decimal number => Create<TTarget, decimal>(number, checkedConversion),
            _ => throw new InvalidOperationException(
                $"Runtime scalar type '{value.GetType().FullName}' is not numeric.")
        };

    private static TTarget Create<TTarget, TSource>(
        TSource value,
        bool checkedConversion)
        where TTarget : INumberBase<TTarget>
        where TSource : INumberBase<TSource> => checkedConversion
            ? TTarget.CreateChecked(value)
            : TTarget.CreateTruncating(value);

    private static bool IsImplicitNumericConversion(
        string source,
        string target,
        DebugExpressionLanguage language)
    {
        if (language == DebugExpressionLanguage.FSharp ||
            language != DebugExpressionLanguage.CSharp &&
            (source is "nint" or "nuint" || target is "nint" or "nuint"))
        {
            return false;
        }

        return source switch
        {
            "sbyte" => target is "short" or "int" or "nint" or "long" or "float" or "double" or
                "decimal",
            "byte" => target is "short" or "ushort" or "int" or "uint" or "nint" or
                "nuint" or "long" or "ulong" or "float" or "double" or "decimal",
            "short" => target is "int" or "nint" or "long" or "float" or "double" or "decimal",
            "ushort" => target is "int" or "uint" or "nint" or "nuint" or "long" or "ulong" or
                "float" or "double" or "decimal",
            "int" => target is "nint" or "long" or "float" or "double" or "decimal",
            "uint" => target is "nuint" or "long" or "ulong" or "float" or "double" or "decimal",
            "long" => target is "float" or "double" or "decimal",
            "ulong" => target is "float" or "double" or "decimal",
            "char" => target is "ushort" or "int" or "uint" or "nint" or "nuint" or "long" or
                "ulong" or "float" or "double" or "decimal",
            "float" => target == "double",
            "nint" => target is "long" or "float" or "double" or "decimal",
            "nuint" => target is "ulong" or "float" or "double" or "decimal",
            _ => false
        };
    }

    private static bool AreLoadedNumericTypes(
        ManagedBoundType source,
        ManagedBoundType target) => source.ModuleId is not null &&
        source.ModuleId == target.ModuleId &&
        IsLoadedNumericType(source) && IsLoadedNumericType(target);

    private static bool IsLoadedNumericType(ManagedBoundType type) =>
        type.ElementType is >= 0x03 and <= 0x0d or 0x18 or 0x19 ||
        type is { ElementType: 0x11, Name: "System.Decimal" };

    private static bool IsIntegral(string type) => type is
        "sbyte" or "byte" or "short" or "ushort" or "int" or "uint" or "long" or
        "ulong" or "nint" or "nuint" or "char";

    private static bool IsNumeric(string type) => IsIntegral(type) || type is
        "float" or "double" or "decimal";

    private static string NormalizeTypeName(
        string typeName,
        DebugExpressionLanguage language) => TryNormalizeTypeName(typeName, language)
        ?? throw new NotSupportedException($"Built-in conversion type '{typeName}' is not supported.");

    /// <summary>
    /// Identifies a supported primitive conversion spelling without interpreting an arbitrary loaded type name.
    /// </summary>
    internal static string? TryNormalizeTypeName(string typeName, DebugExpressionLanguage language)
    {
        string type = typeName.Trim();
        if (type.StartsWith("global::", StringComparison.Ordinal))
        {
            type = type["global::".Length..];
        }

        if (ManagedRuntimeTypeAliases.TryNormalize(type, language, out _, out string debuggerName))
        {
            return debuggerName;
        }

        return language == DebugExpressionLanguage.VisualBasic ? type.ToUpperInvariant() switch
        {
            "CBOOL" => "bool",
            "CSBYTE" => "sbyte",
            "CBYTE" => "byte",
            "CSHORT" => "short",
            "CUSHORT" => "ushort",
            "CINT" => "int",
            "CUINT" => "uint",
            "CLNG" => "long",
            "CULNG" => "ulong",
            "CCHAR" => "char",
            "CSNG" => "float",
            "CDBL" => "double",
            "CDEC" => "decimal",
            "CSTR" => "string",
            "COBJ" => "object",
            _ => null
        } : null;
    }

    private static InvalidOperationException CannotConvert(string source, string target) => new(
        $"Built-in conversion from '{source}' to '{target}' is not supported.");
}
