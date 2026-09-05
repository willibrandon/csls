using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies method ownership and parameter declarations added through compiler-produced metadata deltas.
/// </summary>
public sealed partial class ManagedMetadataImageTests
{
    /// <summary>
    /// Resolves added methods, parameter types and names, and named-tuple attributes from their generation.
    /// </summary>
    [TestMethod]
    public async Task ResolvesAddedMethodsParametersAndTupleAttributes()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-metadata-added-method-");
        try
        {
            (string program, _, _, _, _, byte[] delta, _, _, _, int[] methods) = await HotReloadTestCompilation.EmitAsync(directory.FullName,
                TestContext.CancellationToken, addMethod: true).ConfigureAwait(false);
            using var pe = new PEReader(File.OpenRead(program));
            MetadataReader baseline = pe.GetMetadataReader();
            using var original = new ManagedMetadataImage(baseline, []);
            using var current = new ManagedMetadataImage(baseline, [delta]);
            MethodDefinitionHandle added = MetadataTokens.MethodDefinitionHandle(baseline.MethodDefinitions.Count + 1);
            Assert.IsFalse(original.ContainsMethod(added));
            Assert.IsTrue(current.ContainsMethod(added));
            Assert.IsFalse(current.ContainsMethod(default));
            Assert.IsFalse(current.ContainsMethod(MetadataTokens.MethodDefinitionHandle(baseline.MethodDefinitions.Count + 2)));
            MethodDefinition method = current.GetMethodDefinition(added);
            Assert.AreEqual("Added", current.GetString(method.Name));
            TypeDefinitionHandle owner = current.GetDeclaringType(added);
            Assert.AreEqual("Program", current.GetString(current.GetTypeDefinition(owner).Name));
            Assert.AreEqual(0, current.GetGenericParameterCount(owner));

            MethodDefinitionHandle updated = MetadataTokens.MethodDefinitionHandle(
                Assert.ContainsSingle(methods) & 0x00ffffff);
            Assert.AreEqual(owner, current.GetDeclaringType(updated));
            Assert.AreEqual("Value", current.GetString(current.GetMethodDefinition(updated).Name));
            Assert.IsEmpty(current.GetParameters(updated));
            Assert.AreSequenceEqual(baseline.MethodDefinitions, original.GetMethods());
            Assert.AreSequenceEqual(baseline.MethodDefinitions.Append(added), current.GetMethods());

            IReadOnlyList<ParameterHandle> parameters = current.GetParameters(added);
            Assert.HasCount(3, parameters);
            Assert.AreSequenceEqual(["target", "source", "pair"],
                parameters.Select(handle => current.GetString(current.GetParameter(handle).Name)));
            Assert.AreSequenceEqual([1, 2, 3],
                parameters.Select(handle => current.GetParameter(handle).SequenceNumber));
            MethodSignature<ManagedMetadataTypeSignature> signature = current.DecodeMethodSignature(added, 0);
            Assert.AreEqual(PrimitiveTypeCode.Int32, signature.ReturnType.PrimitiveType);
            Assert.AreEqual(0, signature.GenericParameterCount);
            Assert.HasCount(3, signature.ParameterTypes);
            Assert.AreEqual("System.Exception", signature.ParameterTypes[0].MetadataName);
            Assert.AreEqual("System.ArgumentException", signature.ParameterTypes[1].MetadataName);
            Assert.AreEqual("System.ValueTuple`2", signature.ParameterTypes[2].MetadataName);
            Assert.HasCount(2, signature.ParameterTypes[2].TypeArguments);
            Assert.IsTrue(signature.ParameterTypes[2].TypeArguments.All(static argument =>
                argument.PrimitiveType == PrimitiveTypeCode.Int32));
            Assert.IsNull(ManagedTupleElementNameReader.ReadAttribute(current, parameters[0]));
            ManagedTupleCustomTypeInfo? tuple = ManagedTupleElementNameReader.ReadAttribute(current, parameters[2]);
            Assert.IsNotNull(tuple);
            Assert.AreSequenceEqual(["first", "second"], tuple.TransformNames);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
