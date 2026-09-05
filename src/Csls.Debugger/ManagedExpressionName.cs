using Csls.Debugger.Contracts;
using System.Globalization;

namespace Csls.Debugger;

/// <summary>
/// Constructs language-neutral source expression names for runtime values.
/// </summary>
internal static class ManagedExpressionName
{
    /// <summary>
    /// Preserves an explicit cast in the source path used to inspect or assign one of its members.
    /// </summary>
    internal static string? CreateTypeOperation(
        string? operand, string typeName, DebugExpressionNodeKind kind, DebugExpressionLanguage language)
    {
        if (operand is null)
        {
            return null;
        }

        return language switch
        {
            DebugExpressionLanguage.CSharp => kind == DebugExpressionNodeKind.TryCast
                ? $"({operand} as {typeName})" : $"(({typeName}){operand})",
            DebugExpressionLanguage.VisualBasic => kind == DebugExpressionNodeKind.TryCast
                ? $"TryCast({operand}, {typeName})" : $"DirectCast({operand}, {typeName})",
            DebugExpressionLanguage.FSharp => kind == DebugExpressionNodeKind.ReferenceUpcast
                ? $"({operand} :> {typeName})" : $"({operand} :?> {typeName})",
            _ => null
        };
    }

    /// <summary>
    /// Appends already evaluated array indices using the selected source-language grammar.
    /// </summary>
    /// <param name="parent">The parent source expression, or null when unavailable.</param>
    /// <param name="indices">The concrete source-language array indices.</param>
    /// <param name="language">The owning frame's expression grammar, or null when unavailable.</param>
    /// <returns>The re-evaluable element expression, or null when its grammar is unavailable.</returns>
    internal static string? CreateElement(
        string? parent,
        IReadOnlyList<int> indices,
        DebugExpressionLanguage? language)
    {
        if (parent is null || language is null)
        {
            return null;
        }

        string arguments = string.Join(',', indices.Select(static index =>
            index.ToString(CultureInfo.InvariantCulture)));
        return language switch
        {
            DebugExpressionLanguage.Common or DebugExpressionLanguage.CSharp => $"{parent}[{arguments}]",
            DebugExpressionLanguage.VisualBasic => $"{parent}({arguments})",
            DebugExpressionLanguage.FSharp => $"{parent}.[{arguments}]",
            _ => null
        };
    }

    /// <summary>
    /// Appends a simple member identifier to an existing source expression.
    /// </summary>
    /// <param name="parent">The parent source expression, or null when unavailable.</param>
    /// <param name="name">The runtime member name.</param>
    /// <returns>The member expression, or null when it cannot be represented safely.</returns>
    internal static string? CreateMember(string? parent, string name)
    {
        return parent is null || !IsSimpleIdentifier(name)
            ? null
            : $"{parent}.{name}";
    }

    /// <summary>
    /// Tests whether a runtime name is a language-neutral simple identifier.
    /// </summary>
    /// <param name="value">The runtime name to inspect.</param>
    /// <returns>True when the name can be appended without escaping.</returns>
    internal static bool IsSimpleIdentifier(string value)
    {
        if (value.Length == 0 || !(value[0] == '_' || char.IsLetter(value[0])))
        {
            return false;
        }

        for (int index = 1; index < value.Length; index++)
        {
            if (value[index] != '_' && !char.IsLetterOrDigit(value[index]))
            {
                return false;
            }
        }

        return true;
    }
}
