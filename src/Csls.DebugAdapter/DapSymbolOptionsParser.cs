using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Validates the standard managed-debugger symbolOptions launch object.
/// </summary>
internal static class DapSymbolOptionsParser
{
    /// <summary>
    /// Parses trusted symbol paths, public servers, and an optional cache directory.
    /// </summary>
    /// <param name="arguments">The DAP launch or attach arguments object.</param>
    /// <returns>The validated symbol configuration.</returns>
    internal static DebugSymbolOptions Parse(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("symbolOptions", out JsonElement options))
        {
            return new DebugSymbolOptions();
        }

        if (options.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The symbolOptions value must be an object.");
        }

        return new DebugSymbolOptions
        {
            SearchPaths = ParseSearchPaths(options),
            SearchMicrosoftSymbolServer = GetBoolean(
                options,
                "searchMicrosoftSymbolServer"),
            SearchNuGetOrgSymbolServer = GetBoolean(
                options,
                "searchNuGetOrgSymbolServer"),
            CachePath = GetOptionalString(options, "cachePath"),
            ModuleFilter = ParseModuleFilter(options)
        };
    }

    private static string[] ParseSearchPaths(JsonElement options)
    {
        if (!options.TryGetProperty("searchPaths", out JsonElement paths))
        {
            return [];
        }

        if (paths.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("symbolOptions.searchPaths must be an array of strings.");
        }

        return [.. paths.EnumerateArray().Select(
            static value => value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!
                : throw new ArgumentException(
                    "symbolOptions.searchPaths must contain non-empty strings."))];
    }

    private static DebugSymbolModuleFilterOptions ParseModuleFilter(JsonElement options)
    {
        if (!options.TryGetProperty("moduleFilter", out JsonElement filter))
        {
            return new DebugSymbolModuleFilterOptions();
        }

        if (filter.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("symbolOptions.moduleFilter must be an object.");
        }

        DebugSymbolModuleFilterMode mode = DebugSymbolModuleFilterMode.LoadAllButExcluded;
        if (filter.TryGetProperty("mode", out JsonElement modeValue))
        {
            if (modeValue.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException(
                    "symbolOptions.moduleFilter.mode must be a string.");
            }

            mode = modeValue.GetString() switch
            {
                "loadAllButExcluded" => DebugSymbolModuleFilterMode.LoadAllButExcluded,
                "loadOnlyIncluded" => DebugSymbolModuleFilterMode.LoadOnlyIncluded,
                _ => throw new ArgumentException(
                    "symbolOptions.moduleFilter.mode must be loadAllButExcluded or loadOnlyIncluded.")
            };
        }

        return new DebugSymbolModuleFilterOptions
        {
            Mode = mode,
            ExcludedModules = ParsePatterns(filter, "excludedModules"),
            IncludedModules = ParsePatterns(filter, "includedModules"),
            IncludeSymbolsNextToModules = !filter.TryGetProperty(
                "includeSymbolsNextToModules",
                out _) || GetBoolean(filter, "includeSymbolsNextToModules")
        };
    }

    private static string[] ParsePatterns(JsonElement filter, string name)
    {
        if (!filter.TryGetProperty(name, out JsonElement patterns))
        {
            return [];
        }

        if (patterns.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"symbolOptions.moduleFilter.{name} must be an array.");
        }

        return [.. patterns.EnumerateArray().Select(value =>
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!
                : throw new ArgumentException(
                    $"symbolOptions.moduleFilter.{name} must contain non-empty strings."))];
    }

    private static bool GetBoolean(JsonElement options, string name)
    {
        if (!options.TryGetProperty(name, out JsonElement value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException($"symbolOptions.{name} must be a boolean.")
        };
    }

    private static string? GetOptionalString(JsonElement options, string name)
    {
        if (!options.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : throw new ArgumentException($"symbolOptions.{name} must be a non-empty string.");
    }
}
