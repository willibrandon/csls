using System;
using System.IO;
using System.Threading;

namespace Csls.Testing;

/// <summary>
/// Coordinates real analyzer execution through observable cross-process files.
/// </summary>
public static class AnalyzerExecutionProbeTransport
{
    private const string ReleaseFileName = "AnalyzerExecutionProbe.release";
    /// <summary>
    /// Records one analyzer run and blocks it until release or cancellation.
    /// </summary>
    /// <param name="markerPath">The absolute lifecycle marker path.</param>
    /// <param name="cancellationToken">The real Roslyn analyzer cancellation token.</param>
    public static void WaitForRelease(
        string markerPath,
        CancellationToken cancellationToken)
    {
        string directoryPath = Path.GetDirectoryName(markerPath)
            ?? throw new InvalidOperationException("The analyzer marker has no parent directory.");
        char lastDirectoryCharacter = directoryPath[directoryPath.Length - 1];
        bool hasTrailingSeparator =
            lastDirectoryCharacter == Path.DirectorySeparatorChar ||
            lastDirectoryCharacter == Path.AltDirectorySeparatorChar;
        string releasePath = hasTrailingSeparator
            ? directoryPath + ReleaseFileName
            : directoryPath + Path.DirectorySeparatorChar + ReleaseFileName;
        Signal(markerPath, "started");
        if (!File.Exists(releasePath))
        {
            using var released = new ManualResetEvent(initialState: false);
            using var watcher = new FileSystemWatcher(directoryPath, ReleaseFileName)
            {
                NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.FileName
            };
            FileSystemEventHandler releaseHandler = (_, _) => released.Set();
            RenamedEventHandler renamedHandler = (_, _) => released.Set();
            watcher.Created += releaseHandler;
            watcher.Renamed += renamedHandler;
            watcher.EnableRaisingEvents = true;
            if (File.Exists(releasePath))
            {
                released.Set();
            }

            int signaled = WaitHandle.WaitAny(
                [released, cancellationToken.WaitHandle]);
            watcher.EnableRaisingEvents = false;
            watcher.Created -= releaseHandler;
            watcher.Renamed -= renamedHandler;
            if (signaled == 1)
            {
                Signal(markerPath, "canceled");
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        Signal(markerPath, "released");
    }

    private static void Signal(string markerPath, string value)
    {
        FileSignalPublisher.AppendAllText(markerPath, value + Environment.NewLine);
    }
}
