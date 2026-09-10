using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies fast getter recognition against compiler-produced bodies and hostile file-backed IL.
/// </summary>
[TestClass]
public sealed class ManagedFieldGetterDecoderTests : DapTestContext
{
    /// <summary>
    /// Resolves exact field tokens from Debug and Release getters and rejects target-side writes and finally handlers.
    /// </summary>
    /// <param name="configuration">The compiler configuration of the real process fixture.</param>
    [TestMethod]
    [DataRow("debug")]
    [DataRow("release")]
    public void CompiledGettersResolveOnlyTheirActualReturnedField(string configuration)
    {
        using FileStream stream = File.OpenRead(Path.Join(FindRepositoryRoot(), "artifacts", "bin", "Csls.TestProcessHost",
            configuration, "csls-test-process-host.dll"));
        using var image = new PEReader(stream);
        MetadataReader metadata = image.GetMetadataReader();
        TypeDefinition type = metadata.GetTypeDefinition(metadata.TypeDefinitions.Single(handle =>
            metadata.GetString(metadata.GetTypeDefinition(handle).Name) == "DebuggerFixtureValue"));
        foreach ((string property, string? field) in new (string, string?)[]
        {
            ("NumberProperty", "Number"),
            ("BlockNumberProperty", "Number"),
            ("TextProperty", "Text"),
            ("PairProperty", "Pair"),
            ("MutatingNumberProperty", null),
            ("FinallyNumberProperty", null)
        })
        {
            MethodDefinition method = metadata.GetMethodDefinition(type.GetMethods().Single(handle =>
                metadata.GetString(metadata.GetMethodDefinition(handle).Name) == $"get_{property}"));
            byte[] body = image.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()
                ?? throw new InvalidDataException("The compiled fixture getter has no IL.");
            bool decoded = ManagedFieldGetterDecoder.TryDecode(body, out int token, out int returnLocal);
            Assert.AreEqual(field is not null, decoded, $"{configuration}: {property}");
            if (field is not null)
            {
                EntityHandle actual = MetadataTokens.EntityHandle(token);
                Assert.AreEqual(HandleKind.FieldDefinition, actual.Kind);
                FieldDefinition definition = metadata.GetFieldDefinition((FieldDefinitionHandle)actual);
                Assert.AreEqual(field, metadata.GetString(definition.Name));
                Assert.Contains((FieldDefinitionHandle)actual, type.GetFields());
                Assert.AreEqual(configuration == "debug" && property == "BlockNumberProperty" ? 0 : -1, returnLocal);
                using var current = new ManagedMetadataImage(metadata, []);
                uint locals = checked((uint)MetadataTokens.GetToken(image.GetMethodBody(method.RelativeVirtualAddress).LocalSignature));
                Assert.IsTrue(ManagedFieldGetterSignature.Matches(current, method, definition, locals, returnLocal), property);
            }
            else
            {
                Assert.AreEqual(0, token, property);
                Assert.AreEqual(-1, returnLocal, property);
            }
        }
    }

    /// <summary>
    /// Rejects a return local that narrows the field and a return signature that changes its declared storage type.
    /// </summary>
    /// <param name="mutation">The signature mutation applied to a real compiled assembly file.</param>
    [TestMethod]
    [DataRow("narrow-local")]
    [DataRow("different-return")]
    public async Task HostileFileSignaturesCannotReinterpretGetterValues(string mutation)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Join(FindRepositoryRoot(), "artifacts", "bin",
            "Csls.TestProcessHost", "debug", "csls-test-process-host.dll"), TestContext.CancellationToken).ConfigureAwait(false);
        int methodToken;
        using (var original = new PEReader(new MemoryStream(bytes, writable: false)))
        {
            MetadataReader metadata = original.GetMetadataReader();
            MethodDefinitionHandle handle = metadata.MethodDefinitions.Single(candidate =>
                metadata.GetString(metadata.GetMethodDefinition(candidate).Name) == "get_BlockNumberProperty");
            methodToken = MetadataTokens.GetToken(handle);
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            BlobHandle signature = mutation == "narrow-local"
                ? metadata.GetStandaloneSignature(original.GetMethodBody(method.RelativeVirtualAddress).LocalSignature).Signature
                : method.Signature;
            byte[] payload = metadata.GetBlobBytes(signature);
            Assert.HasCount(3, payload);
            Assert.AreEqual((byte)0x08, payload[^1]);
            int offset = original.PEHeaders.MetadataStartOffset + metadata.GetHeapMetadataOffset(HeapIndex.Blob)
                + MetadataTokens.GetHeapOffset(signature);
            Assert.AreEqual((byte)payload.Length, bytes[offset]);
            bytes[offset + payload.Length] = mutation == "narrow-local" ? (byte)0x05 : (byte)0x0a;
        }
        string directory = Directory.CreateTempSubdirectory("csls-getter-signature-").FullName;
        try
        {
            string path = Path.Join(directory, "hostile.dll");
            await File.WriteAllBytesAsync(path, bytes, TestContext.CancellationToken).ConfigureAwait(false);
            using FileStream stream = File.OpenRead(path);
            using var image = new PEReader(stream);
            MetadataReader metadata = image.GetMetadataReader();
            MethodDefinition method = metadata.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.EntityHandle(methodToken));
            MethodBodyBlock body = image.GetMethodBody(method.RelativeVirtualAddress);
            Assert.IsTrue(ManagedFieldGetterDecoder.TryDecode(body.GetILBytes(), out int token, out int returnLocal));
            using var current = new ManagedMetadataImage(metadata, []);
            Assert.IsFalse(ManagedFieldGetterSignature.Matches(current, method,
                metadata.GetFieldDefinition((FieldDefinitionHandle)MetadataTokens.EntityHandle(token)),
                checked((uint)MetadataTokens.GetToken(body.LocalSignature)), returnLocal), mutation);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Rejects malformed operands, extra execution, mismatched locals, backward control flow and oversized bodies from real files.
    /// </summary>
    /// <param name="mutation">The hostile body shape written across the file boundary.</param>
    [TestMethod]
    [DataRow("truncated-field")]
    [DataRow("invalid-token")]
    [DataRow("nil-field")]
    [DataRow("field-write")]
    [DataRow("mismatched-local")]
    [DataRow("backward-branch")]
    [DataRow("branch-skips-call")]
    [DataRow("extra-call")]
    [DataRow("oversized")]
    public async Task HostileFileBodiesCannotProduceFastGetterPlans(string mutation)
    {
        byte[] fieldRead = [0x02, 0x7b, 0x01, 0x00, 0x00, 0x04];
        byte[] bytes = mutation switch
        {
            "truncated-field" => fieldRead[..4],
            "invalid-token" => [0x02, 0x7b, 0x01, 0x00, 0x00, 0x06, 0x2a],
            "nil-field" => [0x02, 0x7b, 0x00, 0x00, 0x00, 0x04, 0x2a],
            "field-write" => [0x02, 0x7d, 0x01, 0x00, 0x00, 0x04, 0x2a],
            "mismatched-local" => [.. fieldRead, 0x0a, 0x07, 0x2a],
            "backward-branch" => [.. fieldRead, 0x0a, 0x2b, 0xfe, 0x06, 0x2a],
            "branch-skips-call" => [.. fieldRead, 0x0a, 0x2b, 0x05, 0x28, 0x01, 0x00, 0x00, 0x06, 0x06, 0x2a],
            "extra-call" => [.. fieldRead, 0x2a, 0x28, 0x01, 0x00, 0x00, 0x06],
            "oversized" => [.. fieldRead, 0x2a, .. new byte[ManagedFieldGetterDecoder.MaximumMethodBodyBytes - fieldRead.Length]],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        string directory = Directory.CreateTempSubdirectory("csls-getter-il-").FullName;
        try
        {
            string path = Path.Join(directory, "getter.il");
            await File.WriteAllBytesAsync(path, bytes, TestContext.CancellationToken).ConfigureAwait(false);
            byte[] input = await File.ReadAllBytesAsync(path, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsFalse(ManagedFieldGetterDecoder.TryDecode(input, out int fieldToken, out int returnLocal), mutation);
            Assert.AreEqual(0, fieldToken, mutation);
            Assert.AreEqual(-1, returnLocal, mutation);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
