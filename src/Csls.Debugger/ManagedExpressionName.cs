namespace Csls.Debugger;

/// <summary>
/// Constructs language-neutral source expression names for runtime values.
/// </summary>
internal static class ManagedExpressionName
{
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
