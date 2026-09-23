using System.Text;

namespace Csls.Support;

/// <summary>
/// Publishes and resolves a completed VS Code installation for concurrent test readers.
/// </summary>
internal static class VsCodeTestInstallation
{
    /// <summary>
    /// Names the cache-relative executable manifest shared with editor test runners.
    /// </summary>
    internal const string ManifestName = "executable.path";

    private const int MaximumManifestBytes = 4096;
    private static readonly UTF8Encoding s_encoding = new(false, true);

    /// <summary>
    /// Atomically publishes an executable after the upstream installer records completion.
    /// </summary>
    internal static void Publish(string cachePath, string executablePath)
    {
        string root = Path.GetFullPath(cachePath);
        string relativePath = Path.GetRelativePath(root, Path.GetFullPath(executablePath))
            .Replace(Path.DirectorySeparatorChar, '/');
        ValidateExecutable(root, relativePath);
        byte[] content = s_encoding.GetBytes(relativePath);
        if (content.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("The VS Code executable path exceeds the manifest size limit.");
        }

        string temporaryPath = Path.Join(root, $"{ManifestName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
            }

            File.Move(temporaryPath, Path.Join(root, ManifestName), overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// Resolves the provisioned executable after a cache has been restored or relocated.
    /// </summary>
    internal static string Resolve(string cachePath)
    {
        string root = Path.GetFullPath(cachePath);
        using var stream = new FileStream(
            Path.Join(root, ManifestName), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length is <= 0 or > MaximumManifestBytes)
        {
            throw new InvalidDataException("The VS Code executable manifest has an invalid size.");
        }

        byte[] content = new byte[checked((int)stream.Length)];
        stream.ReadExactly(content);
        string relativePath;
        try
        {
            relativePath = s_encoding.GetString(content);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The VS Code executable manifest must contain valid UTF-8.", exception);
        }

        return ValidateExecutable(root, relativePath);
    }

    private static string ValidateExecutable(string root, string relativePath)
    {
        string[] segments = relativePath.Split('/');
        if (segments.Length < 2 || relativePath.Any(char.IsControl) ||
            relativePath.IndexOfAny(['\\', ':']) >= 0 ||
            segments.Any(static segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException("The VS Code executable manifest must contain a cache-relative path.");
        }

        string executablePath = Path.Join(root, Path.Join(segments));
        if (!File.Exists(Path.Join(root, segments[0], "is-complete")) || !File.Exists(executablePath))
        {
            throw new InvalidDataException(
                "The VS Code installation is incomplete. Run scripts/Provision-VsCode.cs.");
        }

        return executablePath;
    }
}
