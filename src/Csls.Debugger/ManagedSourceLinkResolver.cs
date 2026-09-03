using System.Reflection.Metadata;
using System.Text.Json;

namespace Csls.Debugger;

/// <summary>
/// Parses managed PDB Source Link maps and resolves document URIs.
/// </summary>
internal static class ManagedSourceLinkResolver
{
    private const int MaximumMappingCount = 4096;
    private const int MaximumSourceLinkBytes = 1024 * 1024;
    private static readonly Guid s_sourceLinkKind =
        new("CC110556-A091-4D38-9FEC-25AB9A351A6A");

    /// <summary>
    /// Reads and validates the module-level Source Link map once.
    /// </summary>
    /// <param name="reader">The owning Portable PDB metadata reader.</param>
    /// <returns>The mappings ordered from most to least specific.</returns>
    internal static IReadOnlyList<KeyValuePair<string, string>> Read(MetadataReader reader)
    {
        BlobHandle value = FindSourceLinkBlob(reader);
        if (value.IsNil)
        {
            return [];
        }

        BlobReader blob = reader.GetBlobReader(value);
        if (blob.Length > MaximumSourceLinkBytes)
        {
            throw new BadImageFormatException("The PDB Source Link map is too large.");
        }

        try
        {
            return Read(reader.GetBlobBytes(value));
        }
        catch (JsonException exception)
        {
            throw new BadImageFormatException(
                "The PDB Source Link map contains invalid JSON.",
                exception);
        }
    }

    /// <summary>
    /// Parses and validates one raw Source Link JSON document.
    /// </summary>
    /// <param name="sourceLink">The exact UTF-8 Source Link payload.</param>
    /// <returns>The mappings ordered from most to least specific.</returns>
    internal static IReadOnlyList<KeyValuePair<string, string>> Read(
        ReadOnlyMemory<byte> sourceLink)
    {
        if (sourceLink.Length > MaximumSourceLinkBytes)
        {
            throw new BadImageFormatException("The PDB Source Link map is too large.");
        }

        try
        {
            return Parse(sourceLink);
        }
        catch (JsonException exception)
        {
            throw new BadImageFormatException(
                "The PDB Source Link map contains invalid JSON.",
                exception);
        }
    }

    /// <summary>
    /// Resolves one document through a previously parsed Source Link map.
    /// </summary>
    /// <param name="mappings">The validated Source Link mappings.</param>
    /// <param name="documentPath">The exact Portable PDB document path.</param>
    /// <returns>The absolute HTTP URI, or null when no mapping applies.</returns>
    internal static Uri? TryResolve(
        IReadOnlyList<KeyValuePair<string, string>> mappings,
        string documentPath)
    {
        if (documentPath.Contains('*', StringComparison.Ordinal))
        {
            return null;
        }

        foreach ((string pattern, string uriPattern) in mappings)
        {
            if (!TryMap(pattern, uriPattern, documentPath, out string? resolved))
            {
                continue;
            }

            if (!Uri.TryCreate(resolved, UriKind.Absolute, out Uri? result) ||
                result.Scheme is not ("http" or "https") || result.UserInfo.Length != 0)
            {
                return null;
            }

            return result;
        }

        return null;
    }

    private static BlobHandle FindSourceLinkBlob(MetadataReader reader)
    {
        foreach (CustomDebugInformationHandle handle in reader.GetCustomDebugInformation(
            EntityHandle.ModuleDefinition))
        {
            CustomDebugInformation information = reader.GetCustomDebugInformation(handle);
            if (reader.GetGuid(information.Kind) == s_sourceLinkKind)
            {
                return information.Value;
            }
        }

        return default;
    }

    private static List<KeyValuePair<string, string>> Parse(
        ReadOnlyMemory<byte> sourceLink)
    {
        using var json = JsonDocument.Parse(
            sourceLink,
            new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 16 });
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("documents", out JsonElement documents) ||
            documents.ValueKind != JsonValueKind.Object)
        {
            throw new BadImageFormatException("The PDB Source Link map is invalid.");
        }

        var mappings = new List<KeyValuePair<string, string>>();
        foreach (JsonProperty mapping in documents.EnumerateObject())
        {
            if (mappings.Count == MaximumMappingCount ||
                mapping.Value.ValueKind != JsonValueKind.String ||
                !IsValidMapping(mapping.Name, mapping.Value.GetString()!))
            {
                throw new BadImageFormatException(
                    "The PDB Source Link map contains an invalid mapping.");
            }

            mappings.Add(new KeyValuePair<string, string>(
                mapping.Name,
                mapping.Value.GetString()!));
        }

        mappings.Sort(static (left, right) => right.Key.Length.CompareTo(left.Key.Length));
        return mappings;
    }

    private static bool TryMap(
        string pattern,
        string uriPattern,
        string documentPath,
        out string? result)
    {
        result = null;
        int pathWildcard = pattern.IndexOf('*', StringComparison.Ordinal);
        if (pathWildcard < 0)
        {
            if (string.Equals(pattern, documentPath, StringComparison.OrdinalIgnoreCase))
            {
                result = uriPattern;
                return true;
            }

            return false;
        }

        string prefix = pattern[..pathWildcard];
        if (!documentPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string relativePath = string.Join(
            "/",
            documentPath[prefix.Length..]
                .Split(['/', '\\'])
                .Select(Uri.EscapeDataString));
        int uriWildcard = uriPattern.IndexOf('*', StringComparison.Ordinal);
        result = string.Concat(
            uriPattern.AsSpan(0, uriWildcard),
            relativePath,
            uriPattern.AsSpan(uriWildcard + 1));
        return true;
    }

    private static bool IsValidMapping(string pattern, string uriPattern)
    {
        int pathWildcard = pattern.IndexOf('*', StringComparison.Ordinal);
        int uriWildcard = uriPattern.IndexOf('*', StringComparison.Ordinal);
        return pattern.Length > 0 &&
            (pathWildcard < 0 ||
                pathWildcard == pattern.Length - 1 &&
                pattern.LastIndexOf('*') == pathWildcard) &&
            (pathWildcard < 0
                ? uriWildcard < 0
                : uriWildcard >= 0 && uriPattern.LastIndexOf('*') == uriWildcard);
    }
}
