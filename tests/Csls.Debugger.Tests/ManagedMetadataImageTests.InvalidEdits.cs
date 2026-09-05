using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger.Tests;

/// <summary>
/// Rejects malformed edit-log relationships read through real metadata files.
/// </summary>
public sealed partial class ManagedMetadataImageTests
{
    /// <summary>
    /// Rejects invalid method or parameter parents, mismatched children, and incomplete additions.
    /// </summary>
    [TestMethod]
    [DataRow("method-parent", "invalid parent definition")]
    [DataRow("parameter-parent", "invalid parent definition")]
    [DataRow("method-child", "matching child definition")]
    [DataRow("parameter-child", "matching child definition")]
    [DataRow("unfinished", "ends before its child definition")]
    [DataRow("existing-method", "more than one declaring type")]
    public async Task RejectsInvalidEditRelationshipsFromFile(string corruption, string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(corruption);
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-metadata-invalid-edit-");
        try
        {
            (string program, _, _, _, _, byte[] delta, _, _, _, _) = await HotReloadTestCompilation.EmitAsync(
                directory.FullName, TestContext.CancellationToken, addMethod: true).ConfigureAwait(false);
            using (var provider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.Create(delta)))
            {
                MetadataReader reader = provider.GetMetadataReader();
                EditAndContinueLogEntry[] entries = [.. reader.GetEditAndContinueLogEntries()];
                bool parameter = corruption.StartsWith("parameter", StringComparison.Ordinal);
                EditAndContinueOperation operation = parameter
                    ? EditAndContinueOperation.AddParameter : EditAndContinueOperation.AddMethod;
                int row = Array.FindIndex(entries, entry => entry.Operation == operation);
                Assert.IsGreaterThanOrEqualTo(0, row);
                bool child = corruption.EndsWith("child", StringComparison.Ordinal) || corruption == "existing-method";
                int offset = reader.GetTableMetadataOffset(TableIndex.EncLog) +
                    (corruption == "unfinished" ? entries.Length - 1 : row + (child ? 1 : 0)) *
                    reader.GetTableRowSize(TableIndex.EncLog);
                int invalidToken = corruption == "existing-method" ? 0x06000001
                    : corruption == "unfinished" || child || parameter ? 0x02000002 : 0x06000001;
                BinaryPrimitives.WriteInt32LittleEndian(delta.AsSpan(offset), invalidToken);
                if (corruption == "unfinished")
                {
                    BinaryPrimitives.WriteInt32LittleEndian(delta.AsSpan(offset + sizeof(int)),
                        (int)EditAndContinueOperation.AddMethod);
                }
            }

            string path = Path.Join(directory.FullName, "invalid.metadata");
            await File.WriteAllBytesAsync(path, delta, TestContext.CancellationToken).ConfigureAwait(false);
            byte[] invalid = await File.ReadAllBytesAsync(path, TestContext.CancellationToken).ConfigureAwait(false);
            using var pe = new PEReader(File.OpenRead(program));
            BadImageFormatException failure = Assert.ThrowsExactly<BadImageFormatException>(() =>
            {
                using var image = new ManagedMetadataImage(pe.GetMetadataReader(), [invalid]);
            });
            Assert.Contains(diagnostic, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
