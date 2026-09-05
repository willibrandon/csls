using Csls.Debugger.Contracts;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies callable ownership, signatures, and selection against real compiler metadata files.
/// </summary>
[TestClass]
public sealed class ManagedFunctionMethodResolverTests
{
    /// <summary>
    /// Gets the framework-owned cancellation context.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Preserves baseline declarations and added callable identities across repeated compiler updates.
    /// </summary>
    [TestMethod]
    public async Task CurrentMetadataKeepsCallableOwnershipAndSignatures()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-metadata-callables-");
        try
        {
            (string program, _, _, IReadOnlyList<HotReloadDeclarationUpdate> updates) =
                await HotReloadTestCompilation.EmitCallableGenerationsAsync(directory.FullName, TestContext.CancellationToken)
                    .ConfigureAwait(false);
            using var pe = new PEReader(File.OpenRead(program));
            MetadataReader baseline = pe.GetMetadataReader();
            TypeDefinitionHandle programType = baseline.TypeDefinitions.Single(handle =>
                baseline.GetString(baseline.GetTypeDefinition(handle).Name) == "Program");
            TypeDefinitionHandle receiverType = baseline.TypeDefinitions.Single(handle =>
                baseline.GetString(baseline.GetTypeDefinition(handle).Name) == "Receiver");
            MethodDefinitionHandle main = baseline.GetTypeDefinition(programType).GetMethods().Single();
            MethodDefinitionHandle value = baseline.GetTypeDefinition(receiverType).GetMethods().Single(handle =>
                baseline.GetString(baseline.GetMethodDefinition(handle).Name) == "Value");
            MethodDefinitionHandle[]? firstAdded = null;
            for (int generation = 0; generation <= updates.Count; generation++)
            {
                using var metadata = new ManagedMetadataImage(baseline,
                    [.. updates.Take(generation).Select(static update => update.Metadata)]);
                MethodDefinitionHandle[] programMethods = [.. metadata.GetMethods(programType)];
                MethodDefinitionHandle[] receiverMethods = [.. metadata.GetMethods(receiverType)];
                string[] expectedProgramMethods = generation == 0 ? ["Main"] : ["Main", "Added"];
                Assert.AreSequenceEqual(expectedProgramMethods,
                    programMethods.Select(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name)));
                Assert.HasCount(generation == 0 ? 2 : 4, receiverMethods);
                foreach (MethodDefinitionHandle method in programMethods)
                {
                    Assert.AreEqual(programType, metadata.GetDeclaringType(method));
                }
                foreach (MethodDefinitionHandle method in receiverMethods)
                {
                    Assert.AreEqual(receiverType, metadata.GetDeclaringType(method));
                }
                Assert.AreEqual(checked((uint)MetadataTokens.GetToken(main)), ManagedFunctionMethodResolver.Resolve(
                    metadata, checked((uint)MetadataTokens.GetToken(programType)), "Main", DebugExpressionLanguage.CSharp,
                    [], staticMethod: true));
                Assert.IsNull(ManagedFunctionMethodResolver.Resolve(metadata, checked((uint)MetadataTokens.GetToken(programType)),
                    "Main", DebugExpressionLanguage.CSharp, [], staticMethod: false));
                Assert.AreEqual(checked((uint)MetadataTokens.GetToken(value)), ManagedFunctionMethodResolver.Resolve(
                    metadata, checked((uint)MetadataTokens.GetToken(receiverType)), "value", DebugExpressionLanguage.VisualBasic,
                    [], staticMethod: false));
                Assert.IsNull(ManagedFunctionMethodResolver.Resolve(metadata, checked((uint)MetadataTokens.GetToken(receiverType)),
                    "value", DebugExpressionLanguage.CSharp, [], staticMethod: false));
                Assert.IsNull(ManagedFunctionMethodResolver.Resolve(metadata, checked((uint)MetadataTokens.GetToken(receiverType)),
                    "Added", DebugExpressionLanguage.CSharp, [], staticMethod: false));
                if (generation == 0)
                {
                    Assert.AreSequenceEqual(baseline.MethodDefinitions, metadata.GetMethods());
                    continue;
                }

                MethodDefinitionHandle addedStatic = programMethods[1];
                MethodDefinitionHandle addedInstance = Assert.ContainsSingle(receiverMethods.Where(handle =>
                    metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "Added"));
                MethodDefinitionHandle addedConstructor = Assert.ContainsSingle(receiverMethods.Where(handle =>
                    metadata.GetString(metadata.GetMethodDefinition(handle).Name) == ".ctor" &&
                    Decode(metadata, handle).ParameterTypes.SequenceEqual(["string"])));
                MethodDefinitionHandle[] added = [addedStatic, addedInstance, addedConstructor];
                if (firstAdded is null)
                {
                    firstAdded = added;
                }
                else
                {
                    Assert.AreSequenceEqual(firstAdded, added);
                }
                Assert.AreSequenceEqual(baseline.MethodDefinitions.Concat(added.OrderBy(static handle => MetadataTokens.GetRowNumber(handle))),
                    metadata.GetMethods());
                MethodSignature<string> staticSignature = Decode(metadata, addedStatic);
                Assert.AreEqual("reference:System.Exception", staticSignature.ReturnType);
                Assert.AreSequenceEqual(["reference:System.ArgumentException"], staticSignature.ParameterTypes);
                ManagedMetadataTypeSignature returnType = metadata.DecodeMethodSignature(addedStatic, 0).ReturnType;
                var referenceHandle = (AssemblyReferenceHandle)MetadataTokens.EntityHandle(checked((int)returnType.AssemblyReferenceToken));
                AssemblyReference reference = metadata.GetAssemblyReference(referenceHandle);
                AssemblyReferenceHandle originalHandle = baseline.AssemblyReferences.Single(handle =>
                    baseline.GetString(baseline.GetAssemblyReference(handle).Name) == metadata.GetString(reference.Name));
                AssemblyReference original = baseline.GetAssemblyReference(originalHandle);
                Assert.AreNotEqual(originalHandle, referenceHandle);
                Assert.AreEqual(original.Version, reference.Version);
                Assert.AreEqual(original.Flags, reference.Flags);
                Assert.AreEqual(baseline.GetString(original.Culture), metadata.GetString(reference.Culture));
                BlobReader key = metadata.GetBlobReader(reference.PublicKeyOrToken);
                Assert.AreSequenceEqual(baseline.GetBlobBytes(original.PublicKeyOrToken), key.ReadBytes(key.Length));
                MethodSignature<string> instanceSignature = Decode(metadata, addedInstance);
                Assert.AreEqual("int", instanceSignature.ReturnType);
                Assert.AreSequenceEqual(["int"], instanceSignature.ParameterTypes);
                Assert.AreEqual("void", Decode(metadata, addedConstructor).ReturnType);
                foreach (MethodDefinitionHandle method in added)
                {
                    Assert.HasCount(1, metadata.GetParameters(method));
                }
            }
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static MethodSignature<string> Decode(ManagedMetadataImage metadata, MethodDefinitionHandle method)
    {
        BlobReader blob = metadata.GetBlobReader(metadata.GetMethodDefinition(method).Signature);
        var decoder = new SignatureDecoder<string, object?>(new FunctionEvaluationSignatureTypeProvider(metadata),
            metadata.Baseline, genericContext: null);
        return decoder.DecodeMethodSignature(ref blob);
    }
}
