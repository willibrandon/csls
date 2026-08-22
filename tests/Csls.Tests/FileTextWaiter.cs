using System.Text;

namespace Csls.Tests;

/// <summary>
/// Waits for observable text emitted by a real external process.
/// </summary>
internal static class FileTextWaiter
{
    /// <summary>
    /// Waits until a shared file contains the expected text.
    /// </summary>
    internal static async Task WaitAsync(
        string path,
        string expectedText,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string directoryPath = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The observed file has no parent directory.");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directoryPath, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.CreationTime |
                NotifyFilters.LastWrite |
                NotifyFilters.Size
        };
        FileSystemEventHandler changedHandler = (_, _) =>
        {
            if (Contains(path, expectedText))
            {
                completion.TrySetResult();
            }
        };
        watcher.Changed += changedHandler;
        watcher.Created += changedHandler;
        watcher.EnableRaisingEvents = true;
        if (Contains(path, expectedText))
        {
            completion.TrySetResult();
        }

        try
        {
            await completion.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        finally
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= changedHandler;
            watcher.Created -= changedHandler;
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
}
