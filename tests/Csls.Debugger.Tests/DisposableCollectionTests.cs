namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies structured ownership for dynamically acquired disposable resources.
/// </summary>
[TestClass]
public sealed class DisposableCollectionTests
{
    /// <summary>
    /// Disposes every successfully acquired resource when the collection lifetime ends.
    /// </summary>
    [TestMethod]
    public void DisposeReleasesEveryAcquiredResource()
    {
        using var collection = new DisposableCollection<MemoryStream>();
        using MemoryStream first = collection.Acquire(static () => new MemoryStream());
        using MemoryStream second = collection.Acquire(static () => new MemoryStream());

        collection.Dispose();

        Assert.Throws<ObjectDisposedException>(() => first.WriteByte(1));
        Assert.Throws<ObjectDisposedException>(() => second.WriteByte(1));
    }
}
