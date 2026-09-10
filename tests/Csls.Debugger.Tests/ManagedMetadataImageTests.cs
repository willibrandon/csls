using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies aggregate metadata rows and heaps against real compiler-produced generation files.
/// </summary>
[TestClass]
public sealed partial class ManagedMetadataImageTests
{
    /// <summary>
    /// Gets the framework-owned cancellation context.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Reads old and new signatures and updated method names from their owning metadata generations.
    /// </summary>
    [TestMethod]
    public async Task ResolvesOldAndNewDeclarationSignaturesAcrossGenerations()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-metadata-generations-");
        try
        {
            (string program, _, _, IReadOnlyList<HotReloadDeclarationUpdate> updates) =
                await HotReloadTestCompilation.EmitDeclarationGenerationsAsync(directory.FullName, TestContext.CancellationToken)
                    .ConfigureAwait(false);
            using var pe = new PEReader(File.OpenRead(program));
            using var metadata = new ManagedMetadataImage(pe.GetMetadataReader(), [.. updates.Select(static update => update.Metadata)]);
            var provider = new ManagedMetadataTypeSignatureProvider(0, metadata);
            for (int index = 0; index < updates.Count; index++)
            {
                using var delta = MetadataReaderProvider.FromMetadataImage(ImmutableArray.Create(updates[index].Metadata));
                EntityHandle signatureHandle = Assert.ContainsSingle(delta.GetMetadataReader().GetEditAndContinueMapEntries()
                    .Where(static handle => handle.Kind == HandleKind.StandaloneSignature));
                (MetadataReader owner, EntityHandle relative) = metadata.Resolve(signatureHandle);
                StandaloneSignature signature = owner.GetStandaloneSignature((StandaloneSignatureHandle)relative);
                BlobReader blob = metadata.GetBlobReader(signature.Signature);
                var decoder = new SignatureDecoder<ManagedMetadataTypeSignature, object?>(provider, metadata.Baseline, null);
                ImmutableArray<ManagedMetadataTypeSignature> locals = decoder.DecodeLocalSignature(ref blob);
                Assert.HasCount(3, locals);
                Assert.AreEqual(index == 0 ? "System.ArgumentException" : "System.Object", locals[0].MetadataName);
                Assert.AreEqual(index == 0 ? "System.ArgumentException" : "System.ArgumentNullException", locals[1].MetadataName);
                Assert.AreEqual("System.Int32", locals[2].MetadataName);
                Assert.AreEqual(PrimitiveTypeCode.Int32, locals[2].PrimitiveType);
                Assert.IsFalse(locals[0].IsValueType);
                Assert.IsFalse(locals[1].IsValueType);
                Assert.AreEqual(typeof(ArgumentException).Assembly.GetName().Name, locals[1].AssemblyName);
                Assert.AreEqual(0x23000000U, locals[1].AssemblyReferenceToken & 0xff000000U);
            }

            EntityHandle methodHandle = MetadataTokens.EntityHandle(Assert.ContainsSingle(updates[^1].Methods));
            (MetadataReader current, EntityHandle currentHandle) = metadata.Resolve(methodHandle);
            MethodDefinition method = current.GetMethodDefinition((MethodDefinitionHandle)currentHandle);
            Assert.AreEqual("Value", metadata.GetString(method.Name));
            BlobReader methodBlob = metadata.GetBlobReader(method.Signature);
            var methodDecoder = new SignatureDecoder<ManagedMetadataTypeSignature, object?>(provider, metadata.Baseline, null);
            MethodSignature<ManagedMetadataTypeSignature> methodSignature = methodDecoder.DecodeMethodSignature(ref methodBlob);
            Assert.AreEqual(PrimitiveTypeCode.Int32, methodSignature.ReturnType.PrimitiveType);
            Assert.IsEmpty(methodSignature.ParameterTypes);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
