using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Csls.Support;

/// <summary>
/// Retains every debugger dump path while storing identical attachment contents once.
/// </summary>
internal static class DebuggerDumpArchive
{
    /// <summary>
    /// Creates a compressed tar archive with hard-link entries for repeated dump contents.
    /// </summary>
    /// <param name="resultsDirectory">The completed test-result directory to inspect.</param>
    /// <param name="archivePath">The new archive published after all entries have been written.</param>
    /// <param name="cancellationToken">Cancellation that leaves the source evidence intact.</param>
    /// <returns>The retained path count, distinct payload count, and stored payload bytes.</returns>
    internal static async Task<(int Paths, int Payloads, long Bytes)> CreateAsync(
        string resultsDirectory, string archivePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(resultsDirectory);
        if (!Directory.Exists(root))
        {
            return (0, 0, 0);
        }

        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Dump archives require a physical result directory: {root}");
        }

        string[] files = [.. EnumerateDumps(root, cancellationToken).Order(StringComparer.Ordinal)];
        if (files.Length == 0)
        {
            return (0, 0, 0);
        }

        string destination = Path.GetFullPath(archivePath);
        if (File.Exists(destination))
        {
            throw new IOException($"The dump archive already exists: {destination}");
        }

        string parent = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("The dump archive requires a parent directory.", nameof(archivePath));
        Directory.CreateDirectory(parent);
        string pending = Path.Join(parent, $".debugger-dumps-{Guid.NewGuid():N}.tmp");
        var payloads = new Dictionary<(long Length, string Digest), string>();
        long payloadBytes = 0;
        try
        {
            using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var compressed = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            using (var writer = new TarWriter(compressed, leaveOpen: true))
            {
                foreach (string file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                        65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    long length = input.Length;
                    string digest = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)
                        .ConfigureAwait(false));
                    if (input.Length != length)
                    {
                        throw new IOException($"The dump changed while it was being archived: {file}");
                    }

                    string name = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                    if (payloads.TryGetValue((length, digest), out string? original))
                    {
                        var link = new PaxTarEntry(TarEntryType.HardLink, name) { LinkName = original };
                        await writer.WriteEntryAsync(link, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        input.Position = 0;
                        var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = input };
                        await writer.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                        payloads.Add((length, digest), name);
                        payloadBytes = checked(payloadBytes + length);
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(pending, destination);
            return (files.Length, payloads.Count, payloadBytes);
        }
        finally
        {
            File.Delete(pending);
        }
    }

    private static IEnumerable<string> EnumerateDumps(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"Dump archives require physical result paths: {path}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                }
                else if (path.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
                {
                    yield return path;
                }
            }
        }
    }
}
