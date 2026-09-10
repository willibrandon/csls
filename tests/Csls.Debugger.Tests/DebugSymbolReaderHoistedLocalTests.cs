using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies hoisted-local scope boundaries and hostile records through actual Portable PDB files.
/// </summary>
[TestClass]
public sealed class DebugSymbolReaderHoistedLocalTests
{
    private static readonly Guid s_scopeKind = new("6DA9A61E-F8C7-4874-BE62-68BC5630DF71");

    /// <summary>
    /// Gets the active framework context and cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Includes each scope start and last instruction while excluding adjacent offsets.
    /// </summary>
    /// <param name="language">The compiler fixture language.</param>
    /// <param name="configuration">The fixture optimization configuration.</param>
    [TestMethod]
    [DataRow("CSharp", "Debug")]
    [DataRow("CSharp", "Release")]
    [DataRow("VisualBasic", "Debug")]
    [DataRow("VisualBasic", "Release")]
    public void HoistedLocalScopesUseHalfOpenInstructionRanges(string language, string configuration)
    {
        string program = DebuggerLanguageFixtures.GetProgramPath($"Csls.Debugger.Fixtures.{language}", configuration);
        using FileStream peStream = File.OpenRead(program);
        using var pe = new PEReader(peStream, PEStreamOptions.LeaveOpen);
        using FileStream pdbStream = File.OpenRead(Path.ChangeExtension(program, ".pdb"));
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream, MetadataStreamOptions.LeaveOpen);
        MetadataReader pdb = provider.GetMetadataReader();
        uint token = GetCapturedMethod(pe.GetMetadataReader(), pdb);
        CustomDebugInformation information = GetScopeRecord(pdb, token);
        byte[] bytes = pdb.GetBlobBytes(information.Value);
        Assert.IsNotEmpty(bytes);
        using DebugSymbolReader symbols = DebugSymbolReader.TryOpen(program)
            ?? throw new AssertFailedException("The real compiler fixture has no symbols.");
        for (int offset = 0; offset < bytes.Length; offset += 8)
        {
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
            if (length == 0)
            {
                Assert.DoesNotContain(offset / 8, symbols.GetActiveHoistedLocalScopes(token, start));
                continue;
            }

            Assert.Contains(offset / 8, symbols.GetActiveHoistedLocalScopes(token, start));
            Assert.Contains(offset / 8, symbols.GetActiveHoistedLocalScopes(token, checked(start + length - 1)));
            Assert.DoesNotContain(offset / 8, symbols.GetActiveHoistedLocalScopes(token, checked(start + length)));
            if (start > 0)
            {
                Assert.DoesNotContain(offset / 8, symbols.GetActiveHoistedLocalScopes(token, start - 1));
            }
        }
    }

    /// <summary>
    /// Rejects corrupt scope ranges and partial entries after reading a mutated PDB from disk.
    /// </summary>
    /// <param name="damage">The invalid payload component to introduce.</param>
    [TestMethod]
    [DataRow("start")]
    [DataRow("length")]
    [DataRow("partial-entry")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task HoistedLocalScopesRejectMalformedFileRecords(string damage)
    {
        string program = DebuggerLanguageFixtures.GetProgramPath("Csls.Debugger.Fixtures.CSharp", "Debug");
        using FileStream peStream = File.OpenRead(program);
        using var pe = new PEReader(peStream, PEStreamOptions.LeaveOpen);
        byte[] image = await File.ReadAllBytesAsync(Path.ChangeExtension(program, ".pdb"), TestContext.CancellationToken).ConfigureAwait(false);
        using var pdbStream = new MemoryStream(image, writable: false);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream, MetadataStreamOptions.LeaveOpen);
        MetadataReader pdb = provider.GetMetadataReader();
        uint token = GetCapturedMethod(pe.GetMetadataReader(), pdb);
        CustomDebugInformation information = GetScopeRecord(pdb, token);
        int heapOffset = pdb.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(information.Value);
        int prefixLength = (image[heapOffset] & 0x80) == 0 ? 1 : (image[heapOffset] & 0x40) == 0 ? 2 : 4;
        byte[] scopes = pdb.GetBlobBytes(information.Value);
        Assert.IsGreaterThanOrEqualTo(8, scopes.Length);
        Assert.IsTrue(image.AsSpan(heapOffset + prefixLength, scopes.Length).SequenceEqual(scopes));
        if (damage == "partial-entry")
        {
            var length = new BlobBuilder();
            length.WriteCompressedInteger(scopes.Length + 1);
            byte[] prefix = length.ToArray();
            Assert.HasCount(prefixLength, prefix);
            prefix.CopyTo(image, heapOffset);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(heapOffset + prefixLength + (damage == "start" ? 0 : 4)), uint.MaxValue);
        }

        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-hoisted-pdb-");
        string path = Path.Join(directory.FullName, "malformed.pdb");
        try
        {
            await File.WriteAllBytesAsync(path, image, TestContext.CancellationToken).ConfigureAwait(false);
            byte[] actual = await File.ReadAllBytesAsync(path, TestContext.CancellationToken).ConfigureAwait(false);
            using DebugSymbolReader symbols = DebugSymbolReader.TryOpen(actual)
                ?? throw new AssertFailedException("The outer PDB metadata must remain readable.");
            Assert.ThrowsExactly<BadImageFormatException>(() => symbols.GetActiveHoistedLocalScopes(token, 0));
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    private static uint GetCapturedMethod(MetadataReader pe, MetadataReader pdb)
    {
        MethodDefinitionHandle method = Assert.ContainsSingle(pe.MethodDefinitions.Where(handle =>
        {
            MethodDefinitionHandle kickoff = pdb.GetMethodDebugInformation(handle.ToDebugInformationHandle()).GetStateMachineKickoffMethod();
            return !kickoff.IsNil && pe.GetString(pe.GetMethodDefinition(kickoff).Name) == "RunCapturedAsync";
        }));
        return checked((uint)MetadataTokens.GetToken(method));
    }

    private static CustomDebugInformation GetScopeRecord(MetadataReader pdb, uint token) =>
        Assert.ContainsSingle(pdb.GetCustomDebugInformation(MetadataTokens.EntityHandle(checked((int)token)))
            .Select(pdb.GetCustomDebugInformation).Where(info => pdb.GetGuid(info.Kind) == s_scopeKind));
}
