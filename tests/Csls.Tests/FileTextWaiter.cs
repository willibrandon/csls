using System.Text;

namespace Csls.Tests;

/// <summary>
/// Waits for observable text emitted by a real external process.
/// </summary>
internal static class FileTextWaiter
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Waits until a shared file contains the expected text.
    /// </summary>
    internal static async Task WaitAsync(
        string path,
        string expectedText,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        while (!Contains(path, expectedText))
        {
            await Task.Delay(s_pollInterval, timeoutSource.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits until a shared file contains nonempty text and returns it.
    /// </summary>
    internal static async Task<string> WaitForContentsAsync(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        while (true)
        {
            string? contents = ReadNonEmpty(path);
            if (contents is not null)
            {
                return contents;
            }

            await Task.Delay(s_pollInterval, timeoutSource.Token).ConfigureAwait(false);
        }
    }

    private static bool Contains(string path, string expectedText)
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            string contents = reader.ReadToEnd();
            return contents.Contains(expectedText, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string? ReadNonEmpty(string path)
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            string contents = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(contents) ? null : contents;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
