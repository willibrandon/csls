using Csls.Support;
using System.Text;

namespace Csls.Tests;

/// <summary>
/// Verifies atomic, relocatable editor selection through real installation and manifest files.
/// </summary>
[TestClass]
public sealed class VsCodeTestInstallationTests
{
    /// <summary>
    /// Gets the framework cancellation token for concurrent file operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Retains the selected executable when prepared editor assets move to another checkout.
    /// </summary>
    [TestMethod]
    [TestCategory("VsCodeHost")]
    public void PublishedInstallationSurvivesCacheRelocation()
    {
        string root = Directory.CreateTempSubdirectory("csls-vscode-installation-").FullName;
        try
        {
            string cachePath = Path.Join(root, "original");
            string executablePath = CopyInstalledExecutable(cachePath, "selected editor");
            VsCodeTestInstallation.Publish(cachePath, executablePath);
            string relativePath = Path.GetRelativePath(cachePath, executablePath);
            Assert.AreEqual(relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllText(Path.Join(cachePath, VsCodeTestInstallation.ManifestName)));
            Assert.AreEqual(executablePath, VsCodeTestInstallation.Resolve(cachePath));

            string movedPath = Path.Join(root, "relocated checkout");
            Directory.Move(cachePath, movedPath);
            Assert.IsFalse(Directory.Exists(cachePath));
            Assert.AreEqual(Path.Join(movedPath, relativePath), VsCodeTestInstallation.Resolve(movedPath));
            Assert.IsEmpty(Directory.EnumerateFiles(movedPath, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Concurrent readers observe only complete published selections while writers replace the manifest.
    /// </summary>
    [TestMethod]
    [TestCategory("VsCodeHost")]
    public async Task ConcurrentPublicationPreservesCompleteSelections()
    {
        string root = Directory.CreateTempSubdirectory("csls-vscode-installation-").FullName;
        try
        {
            string first = CopyInstalledExecutable(root, "first");
            string second = CopyInstalledExecutable(root, "another completed editor");
            VsCodeTestInstallation.Publish(root, first);
            using (var original = new FileStream(Path.Join(root, VsCodeTestInstallation.ManifestName),
                FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                VsCodeTestInstallation.Publish(root, second);
                Assert.AreEqual(second, VsCodeTestInstallation.Resolve(root));
                using var reader = new StreamReader(original);
                Assert.AreEqual(Path.GetRelativePath(root, first).Replace(Path.DirectorySeparatorChar, '/'),
                    await reader.ReadToEndAsync(TestContext.CancellationToken).ConfigureAwait(false));
            }

            Task[] operations = [.. Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
            {
                for (int iteration = 0; iteration < 32; iteration++)
                {
                    TestContext.CancellationToken.ThrowIfCancellationRequested();
                    VsCodeTestInstallation.Publish(root, worker % 2 == 0 ? first : second);
                    Assert.Contains(VsCodeTestInstallation.Resolve(root), new[] { first, second });
                }
            }, TestContext.CancellationToken))];
            await Task.WhenAll(operations).ConfigureAwait(false);
            VsCodeTestInstallation.Publish(root, first);
            Assert.AreEqual(first, VsCodeTestInstallation.Resolve(root));
            Assert.IsEmpty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A rejected replacement preserves the previous completed executable and its manifest.
    /// </summary>
    [TestMethod]
    [TestCategory("VsCodeHost")]
    public void RejectedPublicationPreservesCompletedInstallation()
    {
        string root = Directory.CreateTempSubdirectory("csls-vscode-installation-").FullName;
        try
        {
            string executablePath = CopyInstalledExecutable(root, "selected");
            VsCodeTestInstallation.Publish(root, executablePath);
            string manifestPath = Path.Join(root, VsCodeTestInstallation.ManifestName);
            string published = File.ReadAllText(manifestPath);
            Assert.ThrowsExactly<InvalidDataException>(() =>
                VsCodeTestInstallation.Publish(root, Path.Join(root, "selected", "absent")));
            Assert.ThrowsExactly<InvalidDataException>(() =>
                VsCodeTestInstallation.Publish(root, Path.Join(root, "..", "outside", "code")));
            Assert.AreEqual(published, File.ReadAllText(manifestPath));
            Assert.AreEqual(executablePath, VsCodeTestInstallation.Resolve(root));
            Assert.IsEmpty(Directory.EnumerateFiles(root, "*.tmp"));

            File.Move(executablePath, executablePath + ".missing");
            Assert.ThrowsExactly<InvalidDataException>(() => VsCodeTestInstallation.Resolve(root));
            File.Move(executablePath + ".missing", executablePath);
            File.Delete(Path.Join(root, "selected", "is-complete"));
            InvalidDataException incomplete = Assert.ThrowsExactly<InvalidDataException>(() =>
                VsCodeTestInstallation.Publish(root, executablePath));
            Assert.Contains("incomplete", incomplete.Message);
            Assert.AreEqual(published, File.ReadAllText(manifestPath));
            Assert.ThrowsExactly<InvalidDataException>(() => VsCodeTestInstallation.Resolve(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Rejects malformed or escaping manifest paths before selecting an executable.
    /// </summary>
    /// <param name="relativePath">The malformed path written through a real file.</param>
    [TestMethod]
    [DataRow("../outside/code")]
    [DataRow("editor/../code")]
    [DataRow("/absolute/code")]
    [DataRow("C:/editor/code.exe")]
    [DataRow("editor\\code.exe")]
    [DataRow("editor//code")]
    [DataRow("editor/./code")]
    [DataRow("editor/code\n")]
    [DataRow("editor/\0code")]
    [DataRow("code")]
    public void RejectsMalformedManifestPaths(string relativePath)
    {
        WithManifest(Encoding.UTF8.GetBytes(relativePath), root =>
        {
            InvalidDataException exception = Assert.ThrowsExactly<InvalidDataException>(() =>
                VsCodeTestInstallation.Resolve(root));
            Assert.Contains("cache-relative path", exception.Message);
        });
    }

    /// <summary>
    /// Distinguishes an absent manifest from invalid UTF-8 and incomplete installed assets.
    /// </summary>
    [TestMethod]
    public void RejectsMissingCorruptAndIncompleteInstallation()
    {
        WithManifest([0xc3, 0x28], root =>
        {
            string manifestPath = Path.Join(root, VsCodeTestInstallation.ManifestName);
            InvalidDataException encoding = Assert.ThrowsExactly<InvalidDataException>(() =>
                VsCodeTestInstallation.Resolve(root));
            Assert.Contains("valid UTF-8", encoding.Message);

            File.WriteAllText(manifestPath, "absent/code");
            InvalidDataException incomplete = Assert.ThrowsExactly<InvalidDataException>(() =>
                VsCodeTestInstallation.Resolve(root));
            Assert.Contains("Run scripts/Provision-VsCode.cs", incomplete.Message);

            File.Delete(manifestPath);
            FileNotFoundException missing = Assert.ThrowsExactly<FileNotFoundException>(() =>
                VsCodeTestInstallation.Resolve(root));
            Assert.AreEqual(manifestPath, missing.FileName);
        });
    }

    /// <summary>
    /// Enforces the manifest byte limit before resolving paths at and around its boundary.
    /// </summary>
    /// <param name="size">The manifest byte length.</param>
    /// <param name="expectedMessage">The validation stage that must reject the file.</param>
    [TestMethod]
    [DataRow(0, "invalid size")]
    [DataRow(4095, "incomplete")]
    [DataRow(4096, "incomplete")]
    [DataRow(4097, "invalid size")]
    public void EnforcesManifestSizeBoundary(int size, string expectedMessage)
    {
        byte[] content = size == 0 ? [] : Encoding.UTF8.GetBytes("editor/" + new string('x', size - 7));
        WithManifest(content, root =>
        {
            InvalidDataException exception = Assert.ThrowsExactly<InvalidDataException>(() =>
                VsCodeTestInstallation.Resolve(root));
            Assert.Contains(expectedMessage, exception.Message);
        });
    }

    private static void WithManifest(byte[] content, Action<string> assertion)
    {
        string root = Directory.CreateTempSubdirectory("csls-vscode-installation-").FullName;
        try
        {
            File.WriteAllBytes(Path.Join(root, VsCodeTestInstallation.ManifestName), content);
            assertion(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CopyInstalledExecutable(string cachePath, string installationName)
    {
        string original = EditorToolResolver.ResolveVsCodeExecutable(EditorToolResolver.FindRepositoryRoot());
        var directory = new DirectoryInfo(Path.GetDirectoryName(original)!);
        while (!File.Exists(Path.Join(directory.FullName, "is-complete")))
        {
            directory = directory.Parent ?? throw new InvalidDataException("The installed editor has no completion marker.");
        }

        // Copy the actual files consumed by selection; editor acceptance launches the complete shared installation.
        string installationRoot = Path.Join(cachePath, installationName);
        string copy = Path.Join(installationRoot, Path.GetRelativePath(directory.FullName, original));
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(original, copy);
        File.Copy(Path.Join(directory.FullName, "is-complete"), Path.Join(installationRoot, "is-complete"));
        return copy;
    }
}
