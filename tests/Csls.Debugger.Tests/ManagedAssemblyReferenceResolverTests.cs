using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies exact same-module assembly-reference equivalence through real compiler and hostile metadata files.
/// </summary>
[TestClass]
public sealed class ManagedAssemblyReferenceResolverTests
{
    /// <summary>
    /// Gets the framework-owned cancellation context.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Preserves every equivalent token once in row order across baseline and repeated metadata generations.
    /// </summary>
    [TestMethod]
    public async Task FindsEquivalentReferencesAcrossGenerations()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-metadata-assembly-references-");
        try
        {
            (string program, _, _, IReadOnlyList<HotReloadDeclarationUpdate> updates) =
                await HotReloadTestCompilation.EmitCallableGenerationsAsync(directory.FullName, TestContext.CancellationToken)
                    .ConfigureAwait(false);
            using var pe = new PEReader(File.OpenRead(program));
            MetadataReader baseline = pe.GetMetadataReader();
            List<AssemblyReferenceHandle> references = [];
            for (int generation = 0; generation <= updates.Count; generation++)
            {
                using var metadata = new ManagedMetadataImage(baseline,
                    [.. updates.Take(generation).Select(static update => update.Metadata)]);
                AssemblyReferenceHandle current = generation == 0
                    ? baseline.AssemblyReferences.Single(handle => baseline.GetString(baseline.GetAssemblyReference(handle).Name) ==
                        typeof(Exception).Assembly.GetName().Name)
                    : GetAddedReturnReference(metadata);
                Assert.DoesNotContain(current, references);
                references.Add(current);
                foreach (AssemblyReferenceHandle reference in references)
                {
                    AssemblyReferenceHandle[] expected = [.. references.Where(handle => handle != reference)];
                    Assert.AreSequenceEqual(expected, ManagedAssemblyReferenceResolver.FindEquivalentReferences(metadata, reference));
                }

                AssemblyReferenceHandle[] all = [.. metadata.GetAssemblyReferences()];
                Assert.AreSequenceEqual(all.OrderBy(static handle => MetadataTokens.GetRowNumber(handle)), all);
                Assert.HasCount(all.Length, all.Distinct());
                foreach (AssemblyReferenceHandle reference in baseline.AssemblyReferences)
                {
                    Assert.Contains(reference, all);
                }

                foreach (AssemblyReferenceHandle reference in references)
                {
                    Assert.Contains(reference, all);
                }
            }
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Refuses an otherwise matching reference after one binding-identity column changes in a metadata file.
    /// </summary>
    [TestMethod]
    [DataRow("version")]
    [DataRow("flags")]
    [DataRow("name")]
    [DataRow("culture")]
    [DataRow("missing-key")]
    [DataRow("different-key")]
    public async Task RejectsDifferentAssemblyIdentityFromFile(string column)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-metadata-assembly-identity-");
        try
        {
            (string program, _, _, IReadOnlyList<HotReloadDeclarationUpdate> updates) =
                await HotReloadTestCompilation.EmitCallableGenerationsAsync(directory.FullName, TestContext.CancellationToken)
                    .ConfigureAwait(false);
            using var pe = new PEReader(File.OpenRead(program));
            MetadataReader baseline = pe.GetMetadataReader();
            byte[] delta = updates[0].Metadata;
            AssemblyReferenceHandle added;
            using (var metadata = new ManagedMetadataImage(baseline, [delta]))
            {
                added = GetAddedReturnReference(metadata);
                Assert.HasCount(1, ManagedAssemblyReferenceResolver.FindEquivalentReferences(metadata, added));
                (MetadataReader reader, EntityHandle relative) = metadata.Resolve(added);
                Assert.AreEqual(28, reader.GetTableRowSize(TableIndex.AssemblyRef));
                int offset = reader.GetTableMetadataOffset(TableIndex.AssemblyRef) +
                    (MetadataTokens.GetRowNumber(relative) - 1) * reader.GetTableRowSize(TableIndex.AssemblyRef);
                AssemblyReference reference = metadata.GetAssemblyReference(added);
                switch (column)
                {
                    case "version":
                        BinaryPrimitives.WriteUInt16LittleEndian(delta.AsSpan(offset), checked((ushort)(reference.Version.Major + 1)));
                        break;
                    case "flags":
                        BinaryPrimitives.WriteInt32LittleEndian(delta.AsSpan(offset + 8), (int)reference.Flags ^ 1);
                        break;
                    case "name":
                        BinaryPrimitives.WriteInt32LittleEndian(delta.AsSpan(offset + 16), 0);
                        break;
                    case "culture":
                        BinaryPrimitives.WriteInt32LittleEndian(delta.AsSpan(offset + 20), MetadataTokens.GetHeapOffset(reference.Name));
                        break;
                    case "missing-key":
                        BinaryPrimitives.WriteInt32LittleEndian(delta.AsSpan(offset + 12), 0);
                        break;
                    case "different-key":
                        AssemblyReference other = baseline.AssemblyReferences.Select(baseline.GetAssemblyReference).First(candidate =>
                            !baseline.GetBlobBytes(candidate.PublicKeyOrToken).SequenceEqual(
                                metadata.GetBlobReader(reference.PublicKeyOrToken).ReadBytes(8)));
                        Assert.HasCount(8, baseline.GetBlobBytes(other.PublicKeyOrToken));
                        BinaryPrimitives.WriteInt32LittleEndian(delta.AsSpan(offset + 12), MetadataTokens.GetHeapOffset(other.PublicKeyOrToken));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(column), column, "Unknown assembly identity column.");
                }
            }

            string path = Path.Join(directory.FullName, "changed-identity.metadata");
            await File.WriteAllBytesAsync(path, delta, TestContext.CancellationToken).ConfigureAwait(false);
            byte[] changed = await File.ReadAllBytesAsync(path, TestContext.CancellationToken).ConfigureAwait(false);
            using var altered = new ManagedMetadataImage(baseline, [changed]);
            Assert.IsEmpty(ManagedAssemblyReferenceResolver.FindEquivalentReferences(altered, added));
            BadImageFormatException failure = Assert.ThrowsExactly<BadImageFormatException>(() =>
                ManagedAssemblyReferenceResolver.FindEquivalentReferences(altered, default));
            Assert.Contains("nonzero metadata row", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static AssemblyReferenceHandle GetAddedReturnReference(ManagedMetadataImage metadata)
    {
        MethodDefinitionHandle method = metadata.GetMethods().Single(handle =>
            metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "Added" &&
            metadata.GetString(metadata.GetTypeDefinition(metadata.GetDeclaringType(handle)).Name) == "Program");
        ManagedMetadataTypeSignature signature = metadata.DecodeMethodSignature(method, 0).ReturnType;
        return (AssemblyReferenceHandle)MetadataTokens.EntityHandle(checked((int)signature.AssemblyReferenceToken));
    }
}
