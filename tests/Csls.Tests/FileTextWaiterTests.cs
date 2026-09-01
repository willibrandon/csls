using System.IO.MemoryMappedFiles;
using System.Text;

namespace Csls.Tests;

/// <summary>
/// Exercises file signal observation through real operating-system file handles.
/// </summary>
[TestClass]
public sealed class FileTextWaiterTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Observes complete text published through atomic file replacement.
    /// </summary>
    [TestMethod]
    public async Task WaitAsyncObservesAtomicFileReplacement()
    {
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-file-waiter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        string markerPath = Path.Join(fixturePath, "signal.marker");
        await File.WriteAllTextAsync(
            markerPath,
            string.Empty,
            TestContext.CancellationToken).ConfigureAwait(false);
        try
        {
            Task waitTask = FileTextWaiter.WaitAsync(
                markerPath,
                "canceled",
                TimeSpan.FromSeconds(5),
                TestContext.CancellationToken);
            string pendingPath = markerPath + ".pending";
            await File.WriteAllTextAsync(
                pendingPath,
                "canceled",
                TestContext.CancellationToken).ConfigureAwait(false);
            File.Replace(pendingPath, markerPath, destinationBackupFileName: null);

            await waitTask.ConfigureAwait(false);
            Assert.AreEqual(
                "canceled",
                await File.ReadAllTextAsync(
                    markerPath,
                    TestContext.CancellationToken).ConfigureAwait(false));
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Observes file content even when the operating system does not publish a watcher event.
    /// </summary>
    [TestMethod]
    public async Task WaitAsyncDoesNotDependOnFileSystemWatcherDelivery()
    {
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-file-waiter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        string markerPath = Path.Join(fixturePath, "signal.marker");
        await File.WriteAllTextAsync(
            markerPath,
            "started ",
            TestContext.CancellationToken).ConfigureAwait(false);
        try
        {
            Task waitTask = FileTextWaiter.WaitAsync(
                markerPath,
                "canceled",
                TimeSpan.FromSeconds(2),
                TestContext.CancellationToken);
            using FileStream stream = new(
                markerPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            using var mappedFile = MemoryMappedFile.CreateFromFile(
                stream,
                mapName: null,
                capacity: stream.Length,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: false);
            using MemoryMappedViewAccessor accessor = mappedFile.CreateViewAccessor(
                0,
                stream.Length,
                MemoryMappedFileAccess.Write);
            byte[] signal = Encoding.UTF8.GetBytes("canceled");
            accessor.WriteArray(0, signal, 0, signal.Length);
            accessor.Flush();

            await waitTask.ConfigureAwait(false);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
