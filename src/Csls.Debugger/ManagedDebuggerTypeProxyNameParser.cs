namespace Csls.Debugger;

/// <summary>
/// Parses bounded assembly-qualified debugger proxy type names without loading target code.
/// </summary>
internal static class ManagedDebuggerTypeProxyNameParser
{
    private const int MaximumTypeNameLength = 4096;
    private const int MaximumNestingDepth = 64;

    /// <summary>
    /// Tries to parse one reflection type name into its definition and assembly identity.
    /// </summary>
    /// <param name="value">The attribute-encoded reflection type name.</param>
    /// <param name="result">Receives the parsed proxy identity.</param>
    /// <returns>True when the bounded name is structurally valid.</returns>
    internal static bool TryParse(
        string value,
        out ManagedDebuggerTypeProxyName? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumTypeNameLength)
        {
            return false;
        }

        if (!TryFindTopLevelSeparator(value, out int assemblySeparator))
        {
            return false;
        }

        ReadOnlySpan<char> type = assemblySeparator < 0
            ? value.AsSpan().Trim()
            : value.AsSpan(0, assemblySeparator).Trim();
        ReadOnlySpan<char> assembly = assemblySeparator < 0
            ? default
            : value.AsSpan(assemblySeparator + 1).Trim();
        if (type.IsEmpty)
        {
            return false;
        }

        int genericArguments = type.IndexOf('[');
        bool isConstructed = genericArguments >= 0;
        ReadOnlySpan<char> metadataName = isConstructed
            ? type[..genericArguments].TrimEnd()
            : type;
        if (metadataName.IsEmpty || metadataName.Contains(']'))
        {
            return false;
        }

        string? assemblyName = null;
        if (!assembly.IsEmpty)
        {
            int qualifier = assembly.IndexOf(',');
            ReadOnlySpan<char> simpleName = qualifier < 0
                ? assembly
                : assembly[..qualifier];
            simpleName = simpleName.Trim();
            if (simpleName.IsEmpty)
            {
                return false;
            }

            assemblyName = simpleName.ToString();
        }

        result = new ManagedDebuggerTypeProxyName(
            metadataName.ToString(),
            assemblyName,
            isConstructed);
        return true;
    }

    private static bool TryFindTopLevelSeparator(string value, out int separator)
    {
        separator = -1;
        int depth = 0;
        for (int index = 0; index < value.Length; index++)
        {
            depth = value[index] switch
            {
                '[' when depth < MaximumNestingDepth => depth + 1,
                ']' when depth > 0 => depth - 1,
                '[' or ']' => -1,
                _ => depth
            };
            if (depth < 0)
            {
                return false;
            }

            if (value[index] == ',' && depth == 0)
            {
                separator = index;
                return true;
            }
        }

        return depth == 0;
    }
}
