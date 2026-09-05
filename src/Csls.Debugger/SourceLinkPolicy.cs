using System.Net;
using System.Net.Sockets;

namespace Csls.Debugger;

/// <summary>
/// Applies configured URL rules and private-network protections to Source Link requests.
/// </summary>
internal sealed class SourceLinkPolicy
{
    private readonly Dictionary<string, bool> _rules = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Replaces the complete set of Source Link URL rules.
    /// </summary>
    /// <param name="rules">URL patterns mapped to enabled states.</param>
    internal void Set(IReadOnlyDictionary<string, bool> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules.Clear();
        foreach ((string pattern, bool enabled) in rules)
        {
            if (string.IsNullOrWhiteSpace(pattern) ||
                pattern.Count(static character => character == '*') > 1)
            {
                throw new ArgumentException(
                    "Source Link URL patterns must be non-empty and contain at most one wildcard.",
                    nameof(rules));
            }

            _rules.Add(pattern, enabled);
        }
    }

    /// <summary>
    /// Rejects disabled, insecure, and implicitly private-network Source Link requests.
    /// </summary>
    /// <param name="uri">The absolute Source Link URI.</param>
    /// <param name="cancellationToken">Cancels host resolution.</param>
    /// <returns>A task that completes when the URI is allowed.</returns>
    internal async Task EnsureAllowedAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        (bool enabled, bool explicitlyConfigured) = Match(uri.AbsoluteUri);
        if (!enabled)
        {
            throw new HttpRequestException("Source Link is disabled for this URL.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !explicitlyConfigured)
        {
            throw new HttpRequestException(
                "HTTP Source Link URLs require an explicit enabled sourceLinkOptions rule.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(
                uri.DnsSafeHost,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            throw new HttpRequestException("The Source Link host could not be resolved.", exception);
        }

        if (!explicitlyConfigured && addresses.Any(IsRestrictedAddress))
        {
            throw new HttpRequestException(
                "Private-network Source Link URLs require an explicit enabled sourceLinkOptions rule.");
        }
    }

    private (bool Enabled, bool ExplicitlyConfigured) Match(string uri)
    {
        string? bestPattern = null;
        bool bestEnabled = true;
        foreach ((string pattern, bool enabled) in _rules)
        {
            if (Matches(pattern, uri) &&
                (bestPattern is null || pattern.Length > bestPattern.Length))
            {
                bestPattern = pattern;
                bestEnabled = enabled;
            }
        }

        return (bestEnabled, bestPattern is not null && bestPattern != "*");
    }

    private static bool Matches(string pattern, string uri)
    {
        int wildcard = pattern.IndexOf('*', StringComparison.Ordinal);
        return wildcard < 0
            ? string.Equals(pattern, uri, StringComparison.OrdinalIgnoreCase)
            : uri.StartsWith(pattern[..wildcard], StringComparison.OrdinalIgnoreCase) &&
                uri.EndsWith(pattern[(wildcard + 1)..], StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRestrictedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal ||
            address.IsIPv6Multicast || address.IsIPv6SiteLocal ||
            address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Any))
        {
            return true;
        }

        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length == 16)
        {
            return (bytes[0] & 0xfe) == 0xfc;
        }

        return bytes[0] is 0 or 10 or 127 or >= 224 ||
            IsLinkLocalAddress(bytes) ||
            IsPrivateAddress(bytes) ||
            IsSharedAddress(bytes);
    }

    private static bool IsLinkLocalAddress(byte[] bytes) =>
        bytes[0] == 169 && bytes[1] == 254;

    private static bool IsPrivateAddress(byte[] bytes) =>
        bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
        bytes[0] == 192 && bytes[1] == 168;

    private static bool IsSharedAddress(byte[] bytes) =>
        bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
}
