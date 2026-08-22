using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

/// <summary>
/// Provides shared infrastructure for csls file-based repository applications.
/// </summary>
internal static class ScriptSupport
{
    /// <summary>
    /// Finds the csls repository root from the including file-based application.
    /// </summary>
    /// <param name="sourceFilePath">The compiler-provided source file path.</param>
    /// <returns>The absolute repository root.</returns>
    internal static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "")
    {
        DirectoryInfo? directory = new(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Csls.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The csls repository root was not found.");
    }

    /// <summary>
    /// Downloads a file and rejects it unless its SHA-256 digest matches the pin.
    /// </summary>
    /// <param name="source">The source URI.</param>
    /// <param name="destinationPath">The destination file path.</param>
    /// <param name="expectedSha256">The expected hexadecimal SHA-256 digest.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task that completes after verification succeeds.</returns>
    internal static async Task DownloadVerifiedFileAsync(
        Uri source,
        string destinationPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CheckCertificateRevocationList = true
        };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("csls-tool-provisioner/0.1");
        using HttpResponseMessage response = await client
            .GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        FileStream destination = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 131_072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (destination.ConfigureAwait(false))
        {
            await response.Content
                .CopyToAsync(destination, cancellationToken)
                .ConfigureAwait(false);
        }

        string actualSha256 = await ComputeSha256Async(
            destinationPath,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"SHA-256 mismatch for {source}: expected {expectedSha256}, got {actualSha256}.");
        }
    }

    /// <summary>
    /// Computes the lowercase hexadecimal SHA-256 digest of a file.
    /// </summary>
    /// <param name="path">The input file path.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The lowercase hexadecimal digest.</returns>
    internal static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        FileStream input = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 131_072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (input.ConfigureAwait(false))
        {
            byte[] digest = await SHA256.HashDataAsync(input, cancellationToken)
                .ConfigureAwait(false);
            return Convert.ToHexStringLower(digest);
        }
    }
}
