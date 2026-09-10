using System.Diagnostics;

namespace Csls.Debugger.Tests;

/// <summary>
/// Deletes debugger fixture directories after operating-system handles are released.
/// </summary>
internal static class DebuggerTestDirectoryReleaseWaiter
{
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Deletes a directory while preserving a bounded assertion on fixture-handle release.
    /// </summary>
    /// <param name="path">The fixture directory to delete.</param>
    /// <param name="timeout">The maximum interval allowed for handle release.</param>
    /// <returns>A task that completes only after the directory is deleted.</returns>
    internal static async Task DeleteAsync(string path, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        long startedTimestamp = Stopwatch.GetTimestamp();
        while (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException &&
                    Stopwatch.GetElapsedTime(startedTimestamp) < timeout)
            {
                if (OperatingSystem.IsWindows() && exception is UnauthorizedAccessException)
                {
                    ClearReadOnlyAttributes(path);
                }

                await Task.Delay(s_retryDelay).ConfigureAwait(false);
            }
        }
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            RecurseSubdirectories = true
        };
        foreach (string filePath in Directory.EnumerateFiles(path, "*", options))
        {
            FileAttributes attributes = File.GetAttributes(filePath);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }
}
