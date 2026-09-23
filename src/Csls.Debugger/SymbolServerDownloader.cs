using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;

namespace Csls.Debugger;

/// <summary>
/// Downloads bounded anonymous symbol-server content across safe redirects.
/// </summary>
internal static class SymbolServerDownloader
{
    private const int MaximumPdbBytes = 256 * 1024 * 1024;
    private const int MaximumRedirects = 5;

    /// <summary>
    /// Downloads one symbol-store index relative to an explicitly selected server.
    /// </summary>
    /// <param name="server">The trusted absolute symbol-server base URI.</param>
    /// <param name="index">The generated slash-separated symbol-store index.</param>
    /// <param name="cancellationToken">Cancels network and content reads.</param>
    /// <returns>The PDB bytes, or null when the server does not contain the index.</returns>
    internal static async Task<byte[]?> DownloadAsync(
        Uri server,
        string index,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            Credentials = null,
            PreAuthenticate = false,
            UseCookies = false
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("csls-debugger", "1"));
        Uri current = BuildUri(server, index);
        for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            using HttpResponseMessage response = await client.GetAsync(
                current,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (IsRedirect(response.StatusCode))
            {
                current = ResolveRedirect(server, current, response.Headers.Location, redirect);
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumPdbBytes)
            {
                throw new InvalidDataException($"Symbol content exceeds {MaximumPdbBytes} bytes.");
            }

            Stream content = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable contentDisposal = content.ConfigureAwait(false);
            using var result = new MemoryStream();
            await CopyBoundedAsync(content, result, cancellationToken).ConfigureAwait(false);
            return result.ToArray();
        }

        throw new HttpRequestException(
            $"Symbol download exceeded the redirect limit of {MaximumRedirects}.");
    }

    private static Uri BuildUri(Uri server, string index)
    {
        string escaped = string.Join(
            '/',
            index.Split('/').Select(Uri.EscapeDataString));
        return new Uri(server, escaped);
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.Moved or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static Uri ResolveRedirect(
        Uri server,
        Uri current,
        Uri? location,
        int redirect)
    {
        if (location is null || redirect == MaximumRedirects)
        {
            throw new HttpRequestException("The symbol server returned an invalid redirect chain.");
        }

        Uri resolved = location.IsAbsoluteUri ? location : new Uri(current, location);
        bool sameAuthority = string.Equals(
            resolved.Authority,
            server.Authority,
            StringComparison.OrdinalIgnoreCase);
        if (!sameAuthority || resolved.Scheme is not ("http" or "https") ||
            server.Scheme == Uri.UriSchemeHttps && resolved.Scheme != Uri.UriSchemeHttps)
        {
            throw new HttpRequestException("The symbol server returned an unsafe redirect URI.");
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
            if (total > MaximumPdbBytes)
            {
                throw new InvalidDataException($"Symbol content exceeds {MaximumPdbBytes} bytes.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
