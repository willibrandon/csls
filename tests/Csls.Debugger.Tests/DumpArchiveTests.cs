using Csls.Debugger.Dump;
using Csls.Support;
using Microsoft.Diagnostics.Runtime;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies lossless debugger evidence archives using captured processes and real attachment copies.
/// </summary>
[TestClass]
public sealed class DumpArchiveTests : DapTestContext
{
    private readonly string _directory = Directory.CreateTempSubdirectory("csls-dump-archive-").FullName;

    /// <summary>
    /// Releases only the directories created by this archive test.
    /// </summary>
    [TestCleanup]
    public Task CleanupAsync() => DebuggerTestDirectoryReleaseWaiter.DeleteAsync(_directory, TimeSpan.FromSeconds(10));

    /// <summary>
    /// Restores original and attachment paths from one complete captured payload.
    /// </summary>
    /// <param name="copies">The number of additional attachment paths containing the same dump.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task DuplicateDumpPathsRestoreOneCapturedPayload(int copies)
    {
        DebuggerDumpFixture fixture = await CreateDumpAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        string results = Directory.CreateDirectory(Path.Join(_directory, "results")).FullName;
        var expected = new List<string> { "capture/target.dmp" };
        CopyDump(fixture.DumpPath, Path.Join(results, expected[0]));
        for (int index = 0; index < copies; index++)
        {
            string attachment = $"_run/In/test-{index}/host with spaces/target.dmp";
            CopyDump(fixture.DumpPath, Path.Join(results, attachment));
            expected.Add(attachment);
        }
        File.Copy(fixture.ProgramPath, Path.Join(results, "fixture.dll"));
        string archive = Path.Join(_directory, "dumps.tar.gz");
        (int paths, int payloads, long bytes) = await DebuggerDumpArchive.CreateAsync(results, archive,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(copies + 1, paths);
        Assert.AreEqual(1, payloads);
        Assert.AreEqual(new FileInfo(fixture.DumpPath).Length, bytes);
        using (FileStream stream = File.OpenRead(archive))
        using (var compressed = new GZipStream(stream, CompressionMode.Decompress))
        using (var reader = new TarReader(compressed))
        {
            var names = new List<string>();
            var stored = new HashSet<string>(StringComparer.Ordinal);
            int links = 0;
            long storedBytes = 0;
            while (await reader.GetNextEntryAsync(cancellationToken: TestContext.CancellationToken)
                .ConfigureAwait(false) is { } entry)
            {
                names.Add(entry.Name);
                if (entry.EntryType == TarEntryType.HardLink)
                {
                    Assert.Contains(entry.LinkName, stored);
                    Assert.AreEqual(0L, entry.Length);
                    links++;
                }
                else
                {
                    Assert.AreEqual(TarEntryType.RegularFile, entry.EntryType);
                    Assert.IsTrue(stored.Add(entry.Name));
                    storedBytes += entry.Length;
                }
            }
            Assert.AreSequenceEqual(expected.Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));
            Assert.AreEqual(copies, links);
            Assert.AreEqual(bytes, storedBytes);
        }

        string restored = Directory.CreateDirectory(Path.Join(_directory, "restored")).FullName;
        await ExtractAsync(archive, restored).ConfigureAwait(false);
        string digest = await GetDigestAsync(fixture.DumpPath).ConfigureAwait(false);
        foreach (string relative in expected)
        {
            string path = Path.Join(restored, relative);
            Assert.AreEqual(digest, await GetDigestAsync(path).ConfigureAwait(false));
            Assert.AreEqual(digest, await GetDigestAsync(Path.Join(results, relative)).ConfigureAwait(false));
            using var target = DataTarget.LoadDump(path);
            Assert.AreEqual(fixture.ProcessId, DumpProcessIdentity.Read(target.DataReader, path,
                TestContext.CancellationToken));
        }
        Assert.IsFalse(File.Exists(Path.Join(restored, "fixture.dll")));
        Assert.IsTrue(File.Exists(Path.Join(results, "fixture.dll")));
    }

    /// <summary>
    /// Retains different captured processes even when their dump filenames match.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task DistinctCapturedDumpsKeepIndependentPayloads()
    {
        DebuggerDumpFixture first = await CreateDumpAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable firstCleanup = first.ConfigureAwait(false);
        DebuggerDumpFixture second = await CreateDumpAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable secondCleanup = second.ConfigureAwait(false);
        string results = Directory.CreateDirectory(Path.Join(_directory, "results")).FullName;
        CopyDump(first.DumpPath, Path.Join(results, "first", "target.dmp"));
        CopyDump(second.DumpPath, Path.Join(results, "second", "target.dmp"));
        string firstDigest = await GetDigestAsync(first.DumpPath).ConfigureAwait(false);
        string secondDigest = await GetDigestAsync(second.DumpPath).ConfigureAwait(false);
        Assert.AreNotEqual(firstDigest, secondDigest);
        string archive = Path.Join(_directory, "dumps.tar.gz");
        (int paths, int payloads, long bytes) = await DebuggerDumpArchive.CreateAsync(results, archive,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(2, paths);
        Assert.AreEqual(2, payloads);
        Assert.AreEqual(new FileInfo(first.DumpPath).Length + new FileInfo(second.DumpPath).Length, bytes);
        string restored = Directory.CreateDirectory(Path.Join(_directory, "restored")).FullName;
        await ExtractAsync(archive, restored).ConfigureAwait(false);
        Assert.AreEqual(firstDigest, await GetDigestAsync(Path.Join(restored, "first", "target.dmp")).ConfigureAwait(false));
        Assert.AreEqual(secondDigest, await GetDigestAsync(Path.Join(restored, "second", "target.dmp")).ConfigureAwait(false));
    }

    /// <summary>
    /// Leaves empty or absent result directories without publishing an archive.
    /// </summary>
    /// <param name="exists">Whether the empty result directory exists.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmptyResultsPublishNoArchive(bool exists)
    {
        string results = Path.Join(_directory, "results");
        if (exists)
        {
            Directory.CreateDirectory(results);
        }
        string archive = Path.Join(_directory, "dumps.tar.gz");
        Assert.AreEqual((0, 0, 0L), await DebuggerDumpArchive.CreateAsync(results, archive,
            TestContext.CancellationToken).ConfigureAwait(false));
        Assert.IsFalse(File.Exists(archive));
        Assert.AreEqual(exists, Directory.Exists(results));
    }

    /// <summary>
    /// Preserves source evidence and an existing archive when publication is canceled or rejected.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CanceledAndExistingArchivesPreserveEvidence()
    {
        DebuggerDumpFixture fixture = await CreateDumpAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        string results = Directory.CreateDirectory(Path.Join(_directory, "results")).FullName;
        string original = Path.Join(results, "target.dmp");
        CopyDump(fixture.DumpPath, original);
        string expected = await GetDigestAsync(original).ConfigureAwait(false);
        string archive = Path.Join(_directory, "dumps.tar.gz");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        OperationCanceledException canceled = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            DebuggerDumpArchive.CreateAsync(results, archive, cancellation.Token)).ConfigureAwait(false);
        Assert.AreEqual(cancellation.Token, canceled.CancellationToken);
        Assert.IsFalse(File.Exists(archive));
        Assert.IsEmpty(Directory.EnumerateFiles(_directory, "*.tmp"));
        _ = await DebuggerDumpArchive.CreateAsync(results, archive, TestContext.CancellationToken).ConfigureAwait(false);
        string archiveDigest = await GetDigestAsync(archive).ConfigureAwait(false);
        _ = await Assert.ThrowsExactlyAsync<IOException>(() => DebuggerDumpArchive.CreateAsync(results, archive,
            TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.AreEqual(archiveDigest, await GetDigestAsync(archive).ConfigureAwait(false));
        Assert.AreEqual(expected, await GetDigestAsync(original).ConfigureAwait(false));
        Assert.IsEmpty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    /// <summary>
    /// Retains a damaged dump separately when its filename and length match the original.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task DamagedSameLengthDumpKeepsItsOwnPayload()
    {
        DebuggerDumpFixture fixture = await CreateDumpAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        string results = Directory.CreateDirectory(Path.Join(_directory, "results")).FullName;
        string original = Path.Join(results, "original", "target.dmp");
        string damaged = Path.Join(results, "damaged", "target.dmp");
        CopyDump(fixture.DumpPath, original);
        CopyDump(fixture.DumpPath, damaged);
        using (FileStream stream = File.Open(damaged, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            int first = stream.ReadByte();
            Assert.IsGreaterThanOrEqualTo(0, first);
            stream.Position = 0;
            stream.WriteByte((byte)(first ^ 0xff));
        }
        string originalDigest = await GetDigestAsync(original).ConfigureAwait(false);
        string damagedDigest = await GetDigestAsync(damaged).ConfigureAwait(false);
        Assert.AreNotEqual(originalDigest, damagedDigest);
        Assert.AreEqual(new FileInfo(original).Length, new FileInfo(damaged).Length);
        string archive = Path.Join(_directory, "dumps.tar.gz");
        (int paths, int payloads, long bytes) = await DebuggerDumpArchive.CreateAsync(results, archive,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(2, paths);
        Assert.AreEqual(2, payloads);
        Assert.AreEqual(2 * new FileInfo(original).Length, bytes);
        string restored = Directory.CreateDirectory(Path.Join(_directory, "restored")).FullName;
        await ExtractAsync(archive, restored).ConfigureAwait(false);
        Assert.AreEqual(originalDigest, await GetDigestAsync(Path.Join(restored, "original", "target.dmp")).ConfigureAwait(false));
        Assert.AreEqual(damagedDigest, await GetDigestAsync(Path.Join(restored, "damaged", "target.dmp")).ConfigureAwait(false));
    }

    /// <summary>
    /// Removes the owned temporary archive when the destination cannot be published.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task FailedPublicationReleasesTemporaryArchive()
    {
        DebuggerDumpFixture fixture = await CreateDumpAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        string results = Directory.CreateDirectory(Path.Join(_directory, "results")).FullName;
        string original = Path.Join(results, "target.dmp");
        CopyDump(fixture.DumpPath, original);
        string expected = await GetDigestAsync(original).ConfigureAwait(false);
        string archive = Directory.CreateDirectory(Path.Join(_directory, "dumps.tar.gz")).FullName;
        _ = await Assert.ThrowsAsync<IOException>(() => DebuggerDumpArchive.CreateAsync(results, archive,
            TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.IsTrue(Directory.Exists(archive));
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(archive));
        Assert.IsEmpty(Directory.EnumerateFiles(_directory, "*.tmp"));
        Assert.AreEqual(expected, await GetDigestAsync(original).ConfigureAwait(false));
    }

    /// <summary>
    /// Rejects links that would include evidence outside the selected result directory.
    /// </summary>
    /// <param name="rootLink">Whether the selected root itself is the symbolic link.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task LinkedResultDirectoriesAreRejected(bool rootLink)
    {
        string results = Path.Join(_directory, "results");
        string outside = Directory.CreateDirectory(Path.Join(_directory, "outside")).FullName;
        string link = rootLink ? results : Path.Join(Directory.CreateDirectory(results).FullName, "linked");
        Directory.CreateSymbolicLink(link, outside);
        string archive = Path.Join(_directory, "dumps.tar.gz");
        IOException rejected = await Assert.ThrowsExactlyAsync<IOException>(() =>
            DebuggerDumpArchive.CreateAsync(results, archive, TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.Contains(link, rejected.Message);
        Assert.IsFalse(File.Exists(archive));
        Assert.IsTrue(Directory.Exists(outside));
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(outside));
    }

    private Task<DebuggerDumpFixture> CreateDumpAsync() => DebuggerDumpFixture.CreateAsync(
        ResolveTestProcessHost(), TestContext.CancellationToken, diagnosticContext: TestContext);

    private async Task ExtractAsync(string archive, string destination)
    {
        using FileStream stream = File.OpenRead(archive);
        using var compressed = new GZipStream(stream, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(compressed, destination, overwriteFiles: false,
            TestContext.CancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetDigestAsync(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.CancellationToken).ConfigureAwait(false));
    }

    private static void CopyDump(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The copied dump needs a parent directory."));
        File.Copy(source, destination);
    }
}
