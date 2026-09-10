using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies scoped type lookup against installed runtime metadata read through real files.
/// </summary>
[TestClass]
public sealed class ManagedMetadataTypeLookupTests
{
    /// <summary>
    /// Finds nested generic definitions only in their selected assembly scope.
    /// </summary>
    [TestMethod]
    public void NestedTypeLookupPreservesAssemblyScope()
    {
        using FileStream stream = File.OpenRead(typeof(Dictionary<,>).Assembly.Location);
        using var image = new PEReader(stream);
        MetadataReader metadata = image.GetMetadataReader();
        string assembly = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        int scanned = 0;
        const string TypeName = "System.Collections.Generic.Dictionary`2+KeyCollection";
        uint token = Assert.IsInstanceOfType<uint>(
            ManagedMetadataTypeLookup.FindDefinition(metadata, TypeName, assembly, ref scanned));
        TypeDefinition definition = metadata.GetTypeDefinition(
            MetadataTokens.TypeDefinitionHandle(checked((int)(token & 0x00ffffff))));
        Assert.AreEqual("KeyCollection", metadata.GetString(definition.Name));
        Assert.AreEqual("Dictionary`2", metadata.GetString(metadata.GetTypeDefinition(definition.GetDeclaringType()).Name));
        Assert.IsGreaterThan(0, scanned);
        int before = scanned;
        Assert.IsNull(ManagedMetadataTypeLookup.FindDefinition(metadata, TypeName,
            nameof(ManagedMetadataTypeLookupTests), ref scanned));
        Assert.AreEqual(before, scanned);
        Assert.IsNull(ManagedMetadataTypeLookup.FindDefinition(metadata,
            typeof(ManagedMetadataTypeLookupTests).FullName ?? throw new InvalidOperationException("No test type name."),
            assembly, ref scanned));
    }

    /// <summary>
    /// Accepts the final metadata scan slot and rejects the first slot beyond its budget.
    /// </summary>
    [TestMethod]
    public void TypeLookupEnforcesExactScanBudget()
    {
        using FileStream stream = File.OpenRead(typeof(object).Assembly.Location);
        using var image = new PEReader(stream);
        MetadataReader metadata = image.GetMetadataReader();
        int scanned = 999_999;
        Assert.AreEqual(0x02000001u, ManagedMetadataTypeLookup.FindDefinition(metadata, "<Module>", null, ref scanned));
        Assert.AreEqual(1_000_000, scanned);
        InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(() =>
            ManagedMetadataTypeLookup.FindDefinition(metadata, "<Module>", null, ref scanned));
        Assert.Contains("1000000", failure.Message);
        Assert.AreEqual(1_000_001, scanned);
    }

    /// <summary>
    /// Returns the exact assembly-reference token recorded by the installed runtime facade.
    /// </summary>
    [TestMethod]
    public void ForwardedTypeLookupPreservesAssemblyReference()
    {
        string path = Path.Join(Path.GetDirectoryName(typeof(object).Assembly.Location), "System.Runtime.dll");
        using FileStream stream = File.OpenRead(path);
        using var image = new PEReader(stream);
        MetadataReader metadata = image.GetMetadataReader();
        uint reference = ManagedMetadataTypeLookup.GetForwardedAssemblyReference(metadata, "System.ValueTuple`2");
        Assert.AreEqual(0x23000000u, reference & 0xff000000);
        Assert.IsGreaterThan(0u, reference & 0x00ffffff);
        AssemblyReference assembly = metadata.GetAssemblyReference(
            MetadataTokens.AssemblyReferenceHandle(checked((int)(reference & 0x00ffffff))));
        Assert.AreEqual("System.Private.CoreLib", metadata.GetString(assembly.Name));
        Assert.AreEqual(0u, ManagedMetadataTypeLookup.GetForwardedAssemblyReference(metadata,
            nameof(ManagedMetadataTypeLookupTests)));
    }

    /// <summary>
    /// Bounds deeply nested hostile metadata read from a real metadata file.
    /// </summary>
    /// <param name="depth">The nested declaration depth immediately around the inspection limit.</param>
    [TestMethod]
    [DataRow(255)]
    [DataRow(256)]
    [DataRow(257)]
    public void TypeLookupBoundsNestedMetadata(int depth)
    {
        var builder = new MetadataBuilder();
        _ = builder.AddModule(0, builder.GetOrAddString("nested-metadata"), default, default, default);
        _ = builder.AddTypeDefinition(TypeAttributes.NotPublic, default, builder.GetOrAddString("<Module>"),
            default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        TypeDefinitionHandle parent = default;
        for (int index = 0; index < depth; index++)
        {
            TypeDefinitionHandle current = builder.AddTypeDefinition(index == 0 ? TypeAttributes.Public : TypeAttributes.NestedPublic,
                index == 0 ? builder.GetOrAddString("Hostile") : default, builder.GetOrAddString("Nested"),
                default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            if (!parent.IsNil)
            {
                builder.AddNestedType(current, parent);
            }
            parent = current;
        }
        var blob = new BlobBuilder();
        new MetadataRootBuilder(builder).Serialize(blob, 0, 0);
        string directory = Directory.CreateTempSubdirectory("csls-nested-type-metadata-").FullName;
        try
        {
            string path = Path.Join(directory, "nested.metadata");
            File.WriteAllBytes(path, blob.ToArray());
            using FileStream stream = File.OpenRead(path);
            using var provider = MetadataReaderProvider.FromMetadataStream(stream);
            MetadataReader metadata = provider.GetMetadataReader();
            string name = $"Hostile.{string.Join('+', Enumerable.Repeat("Nested", depth))}";
            int scanned = 0;
            if (depth <= 256)
            {
                Assert.AreEqual(checked((uint)MetadataTokens.GetToken(parent)),
                    ManagedMetadataTypeLookup.FindDefinition(metadata, name, null, ref scanned));
                Assert.AreEqual(depth + 1, scanned);
            }
            else
            {
                BadImageFormatException failure = Assert.ThrowsExactly<BadImageFormatException>(() =>
                    ManagedMetadataTypeLookup.FindDefinition(metadata, name, null, ref scanned));
                Assert.Contains("256 nested levels", failure.Message);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
