using Csls.Debugger.Contracts;
using Microsoft.CodeAnalysis.Text;
using System.Security.Cryptography;
using System.Text;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies bounded checksum validation through real local source files.
/// </summary>
[TestClass]
public sealed class SourceChecksumVerifierTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Accepts original source and rejects modified, missing, and unsupported-checksum files.
    /// </summary>
    /// <param name="algorithm">The Portable PDB checksum algorithm.</param>
    [TestMethod]
    [DataRow("SHA1")]
    [DataRow("SHA256")]
    public async Task LocalSourceChecksumTracksFileContent(string algorithm)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-source-checksum-");
        string path = Path.Join(directory.FullName, "DebuggerFixture.cs");
        try
        {
            string original = Path.Join(DebuggerTestEnvironment.FindRepositoryRoot(), "tests",
                "Csls.TestProcessHost", "DebuggerFixture.cs");
            byte[] source = await File.ReadAllBytesAsync(original, TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(path, source, TestContext.CancellationToken).ConfigureAwait(false);
            string digest;
            using (FileStream file = File.OpenRead(original))
            {
                var compilerSource = SourceText.From(file, Encoding.UTF8,
                    algorithm == "SHA1" ? SourceHashAlgorithm.Sha1 : SourceHashAlgorithm.Sha256);
                digest = Convert.ToHexStringLower(compilerSource.GetChecksum().AsSpan());
            }

            var checksum = new DebugSourceChecksum(algorithm, digest);
            Assert.IsTrue(SourceChecksumVerifier.MatchesFile(path, checksum));
            Assert.IsTrue(SourceChecksumVerifier.MatchesFile(path, checksum with { Value = digest.ToUpperInvariant() }));
            Assert.IsTrue(SourceChecksumVerifier.MatchesFile(path, checksum: null));
            Assert.IsFalse(SourceChecksumVerifier.MatchesFile(path, new DebugSourceChecksum("unsupported", digest)));

            await File.AppendAllTextAsync(path, Environment.NewLine, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsFalse(SourceChecksumVerifier.MatchesFile(path, checksum));
            File.Delete(path);
            Assert.IsFalse(SourceChecksumVerifier.MatchesFile(path, checksum));
            Assert.IsFalse(SourceChecksumVerifier.MatchesFile(directory.FullName, checksum));
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    /// <summary>
    /// Enforces the local-source byte limit even when an oversized file has a matching checksum.
    /// </summary>
    /// <param name="offset">The file length relative to the source-byte limit.</param>
    [TestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(1)]
    public void LocalSourceByteLimitIsInclusive(int offset)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-source-boundary-");
        string path = Path.Join(directory.FullName, "oversized.cs");
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                file.SetLength(32 * 1024 * 1024 + offset);
            }

            string digest;
            using (FileStream file = File.OpenRead(path))
            {
                digest = Convert.ToHexString(SHA256.HashData(file));
            }

            Assert.AreEqual(offset <= 0, SourceChecksumVerifier.MatchesFile(path, new DebugSourceChecksum("SHA256", digest)));
            Assert.AreEqual(offset <= 0, SourceChecksumVerifier.MatchesFile(path, checksum: null));
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }
}
