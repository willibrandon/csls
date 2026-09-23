using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Applies standard managed-debugger module wildcard filtering.
/// </summary>
internal sealed class SymbolModuleFilter
{
    private DebugSymbolModuleFilterMode _mode;
    private string[] _excluded = [];
    private string[] _included = [];
    private bool _includeAdjacent = true;

    /// <summary>
    /// Replaces the complete module-name symbol policy.
    /// </summary>
    /// <param name="options">The standard inclusion or exclusion options.</param>
    internal void Set(DebugSymbolModuleFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Mode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The symbol module filter mode is not supported.");
        }

        Validate(options.ExcludedModules);
        Validate(options.IncludedModules);
        _mode = options.Mode;
        _excluded = [.. options.ExcludedModules];
        _included = [.. options.IncludedModules];
        _includeAdjacent = options.IncludeSymbolsNextToModules;
    }

    /// <summary>
    /// Gets whether adjacent and embedded symbols may be inspected for a module.
    /// </summary>
    /// <param name="modulePath">The absolute managed module path.</param>
    /// <returns>True when default symbol locations remain eligible.</returns>
    internal bool AllowsAdjacent(string modulePath) => AllowsSearch(modulePath) || _includeAdjacent;

    /// <summary>
    /// Gets whether configured directories and servers may be searched for a module.
    /// </summary>
    /// <param name="modulePath">The absolute managed module path.</param>
    /// <returns>True when configured symbol locations are eligible.</returns>
    internal bool AllowsSearch(string modulePath)
    {
        string moduleName = Path.GetFileName(modulePath);
        return _mode switch
        {
            DebugSymbolModuleFilterMode.LoadAllButExcluded =>
                !_excluded.Any(pattern => Matches(pattern, moduleName)),
            DebugSymbolModuleFilterMode.LoadOnlyIncluded =>
                _included.Any(pattern => Matches(pattern, moduleName)),
            _ => throw new InvalidOperationException("The symbol module filter mode is invalid.")
        };
    }

    private static void Validate(IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        if (patterns.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Symbol module patterns must be non-empty strings.");
        }
    }

    private static bool Matches(string pattern, string value)
    {
        int patternIndex = 0;
        int valueIndex = 0;
        int wildcard = -1;
        int retry = -1;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length &&
                char.ToUpperInvariant(pattern[patternIndex]) == char.ToUpperInvariant(value[valueIndex]))
            {
                patternIndex++;
                valueIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                wildcard = patternIndex++;
                retry = valueIndex;
            }
            else if (wildcard >= 0)
            {
                patternIndex = wildcard + 1;
                valueIndex = ++retry;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }
}
