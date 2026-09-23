using System.Net;
using System.Net.Http.Headers;

namespace Csls.Debugger;

/// <summary>
/// Downloads bounded Source Link content without credentials or implicit redirects.
/// </summary>
internal static class SourceLinkSourceDownloader
{
    private const int MaximumSourceBytes = 32 * 1024 * 1024;
    private const int MaximumRedirects = 5;

    /// <summary>
    /// Downloads one Source Link URI and enforces its bounded redirect chain.
    /// </summary>
    /// <param name="sourceUri">The absolute HTTP Source Link URI.</param>
    /// <param name="policy">The URL and network boundary policy.</param>
    /// <param name="cancellationToken">Cancels network and content reads.</param>
    /// <returns>The downloaded source bytes.</returns>
    internal static async Task<byte[]> DownloadAsync(
        Uri sourceUri,
        SourceLinkPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceUri);
        ArgumentNullException.ThrowIfNull(policy);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            Credentials = null,
            PreAuthenticate = false,
            UseCookies = false
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("csls-debugger", "1"));

        Uri current = sourceUri;
        for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            await policy.EnsureAllowedAsync(current, cancellationToken).ConfigureAwait(false);
            using HttpResponseMessage response = await client.GetAsync(
                current,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                current = ResolveRedirect(current, response.Headers.Location, redirect);
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumSourceBytes)
            {
                throw new InvalidDataException(
                    $"Source Link content exceeds {MaximumSourceBytes} bytes.");
            }

            using Stream content = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var result = new MemoryStream();
            await CopyBoundedAsync(content, result, cancellationToken).ConfigureAwait(false);
            return result.ToArray();
        }

        throw new HttpRequestException(
            $"Source Link exceeded the redirect limit of {MaximumRedirects}.");
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.Moved or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static Uri ResolveRedirect(Uri current, Uri? location, int redirect)
    {
        if (location is null || redirect == MaximumRedirects)
        {
            throw new HttpRequestException("Source Link returned an invalid redirect chain.");
        }

        Uri resolved = location.IsAbsoluteUri ? location : new Uri(current, location);
        if (resolved.Scheme is not ("http" or "https") ||
            current.Scheme == Uri.UriSchemeHttps && resolved.Scheme != Uri.UriSchemeHttps)
        {
            throw new HttpRequestException("Source Link returned an unsafe redirect URI.");
        }

        return resolved;
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        int total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            total = checked(total + read);
            if (total > MaximumSourceBytes)
            {
                throw new InvalidDataException(
                    $"Source Link content exceeds {MaximumSourceBytes} bytes.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
