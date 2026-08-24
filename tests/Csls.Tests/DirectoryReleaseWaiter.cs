using System.Diagnostics;

namespace Csls.Tests;

/// <summary>
/// Deletes a test directory after external processes release operating-system handles.
/// </summary>
internal static class DirectoryReleaseWaiter
{
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Deletes a directory recursively while transient external-process handles are released.
    /// </summary>
    /// <param name="path">The directory to delete.</param>
    /// <param name="timeout">The maximum interval to wait for handle release.</param>
    /// <returns>A task that completes after the directory is deleted.</returns>
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
                await Task.Delay(s_retryDelay).ConfigureAwait(false);
            }
        }
    }
}
