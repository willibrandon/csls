namespace Csls.Tests;

/// <summary>
/// Exercises directory cleanup against real graphical editor filesystem state.
/// </summary>
[TestClass]
public sealed class DirectoryReleaseWaiterTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Verifies production cleanup deletes a real nested read-only file.
    /// </summary>
    [TestMethod]
    public async Task DeletesDirectoryContainingReadOnlyFiles()
    {
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-directory-release-{Guid.NewGuid():N}");
        string filePath = Path.Join(fixturePath, "nested", "read-only.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(
            filePath,
            "{}\n",
            TestContext.CancellationToken).ConfigureAwait(false);
        File.SetAttributes(filePath, File.GetAttributes(filePath) | FileAttributes.ReadOnly);
        try
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            Assert.IsFalse(Directory.Exists(fixturePath));
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.SetAttributes(filePath, FileAttributes.Normal);
            }

            if (Directory.Exists(fixturePath))
            {
                Directory.Delete(fixturePath, recursive: true);
            }
        }
    }
}
