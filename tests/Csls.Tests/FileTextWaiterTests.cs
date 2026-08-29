using System.Runtime.CompilerServices;
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
    /// Observes text written while another process-equivalent handle excludes readers.
    /// </summary>
    [TestMethod]
    public async Task WaitAsyncObservesTextAfterExclusiveWriterReleasesFile()
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
            await Task.Delay(
                TimeSpan.FromMilliseconds(100),
                TestContext.CancellationToken).ConfigureAwait(false);
            {
                var writer = new FileStream(
                    markerPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None);
                await using ConfiguredAsyncDisposable writerCleanup =
                    writer.ConfigureAwait(false);
                await writer.WriteAsync(
                    "canceled"u8.ToArray(),
                    TestContext.CancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(TestContext.CancellationToken).ConfigureAwait(false);
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsFalse(waitTask.IsCompleted);
            }

            await waitTask.ConfigureAwait(false);
            Assert.AreEqual(
                "canceled",
                await File.ReadAllTextAsync(
                    markerPath,
                    Encoding.UTF8,
                    TestContext.CancellationToken).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }
}
