namespace Csls.Debugger;

/// <summary>
/// Implements safe local storage and identity validation for managed PDB lookup.
/// </summary>
internal sealed partial class DebugSymbolLocator
{
    private void AddSearchPath(string searchPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchPath);
        if (Path.IsPathFullyQualified(searchPath))
        {
            _directories.Add(Path.GetFullPath(searchPath));
            return;
        }

        if (!Uri.TryCreate(searchPath, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "Symbol search paths must be absolute directories or anonymous HTTP(S) base URLs.");
        }

        _servers.Add(new Uri(uri.AbsoluteUri.TrimEnd('/') + '/', UriKind.Absolute));
    }

    private static string? FindLocalMatch(
        string modulePath,
        CodeViewSymbolReference reference,
        string directory)
    {
        foreach (string candidate in new[]
        {
            Path.Join(directory, reference.FileName),
            GetStorePath(directory, reference.FileName, reference.PortableIdentity),
            GetStorePath(directory, reference.FileName, reference.WindowsIdentity)
        })
        {
            if (IsMatch(modulePath, candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static async Task<string?> DownloadAndCacheAsync(
        Uri server,
        string modulePath,
        string cacheFile,
        string index,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[]? content = await SymbolServerDownloader.DownloadAsync(
                server,
                index,
                cancellationToken).ConfigureAwait(false);
            return content is null
                ? null
                : await CacheIfValidAsync(
                    modulePath,
                    cacheFile,
                    content,
                    cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<string?> CacheIfValidAsync(
        string modulePath,
        string cacheFile,
        byte[] content,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(cacheFile)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Join(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, content, cancellationToken)
                .ConfigureAwait(false);
            if (!IsMatch(modulePath, temporary))
            {
                return null;
            }

            try
            {
                File.Move(temporary, cacheFile, overwrite: false);
            }
            catch (IOException) when (File.Exists(cacheFile))
            {
            }

            return IsMatch(modulePath, cacheFile) ? cacheFile : null;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }

    private static bool IsMatch(string modulePath, string symbolPath)
    {
        if (!File.Exists(symbolPath))
        {
            return false;
        }

        try
        {
            using var symbols = DebugSymbolReader.TryOpen(
                modulePath,
                symbolPath);
            return symbols is not null;
        }
        catch (Exception exception) when (DebugSymbolReader.IsReadFailure(exception))
        {
            return false;
        }
    }

    private static DebugSymbolReader? TryOpen(string modulePath, string? symbolPath)
    {
        try
        {
            return DebugSymbolReader.TryOpen(modulePath, symbolPath);
        }
        catch (Exception exception) when (DebugSymbolReader.IsReadFailure(exception))
        {
            return null;
        }
    }

    private static CodeViewSymbolReference? TryReadReference(string modulePath)
    {
        try
        {
            return PortablePdbReader.ReadCodeViewReference(modulePath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return null;
        }
    }

}
