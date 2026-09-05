using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies declaration signatures using real compiler-produced assembly files.
/// </summary>
[TestClass]
public sealed class ManagedMetadataTypeSignatureProviderTests
{
    /// <summary>
    /// Gets the framework-owned cancellation context.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Separates containing-type parameters from method parameters with the same index.
    /// </summary>
    [TestMethod]
    public void GenericParametersPreserveTheirDeclaringScope()
    {
        WithMetadata("""
            public class Container<T>
            {
                public static U Convert<U>(T source, U replacement) => replacement;
            }
            """, (reader, _) =>
        {
            MethodDefinition method = FindMethod(reader, "Convert");
            MethodSignature<ManagedMetadataTypeSignature> signature = method.DecodeSignature(
                ManagedMetadataTypeSignatureProvider.Instance, genericContext: null);
            Assert.HasCount(2, signature.ParameterTypes);
            Assert.AreEqual(0, signature.ParameterTypes[0].GenericTypeParameterIndex);
            Assert.IsNull(signature.ParameterTypes[0].GenericMethodParameterIndex);
            Assert.AreEqual(0, signature.ParameterTypes[1].GenericMethodParameterIndex);
            Assert.IsNull(signature.ParameterTypes[1].GenericTypeParameterIndex);
            Assert.AreEqual(0, signature.ReturnType.GenericMethodParameterIndex);
            Assert.IsNull(signature.ReturnType.GenericTypeParameterIndex);
        });
    }

    /// <summary>
    /// Decodes all local slots without confusing a by-reference slot with its element type.
    /// </summary>
    [TestMethod]
    public void ByReferenceLocalDoesNotPreventReadingOtherDeclarations()
    {
        WithMetadata("""
            public static class Container
            {
                public static void Run(ref string input)
                {
                    ref string alias = ref input;
                    System.Exception target = new System.ArgumentException();
                    alias = target.Message;
                    System.GC.KeepAlive(target);
                }
            }
            """, (reader, pe) =>
        {
            MethodDefinition method = FindMethod(reader, "Run");
            StandaloneSignatureHandle locals = pe.GetMethodBody(method.RelativeVirtualAddress).LocalSignature;
            ImmutableArray<ManagedMetadataTypeSignature> signatures = reader.GetStandaloneSignature(locals).DecodeLocalSignature(
                ManagedMetadataTypeSignatureProvider.Instance, genericContext: null);
            ManagedMetadataTypeSignature alias = Assert.ContainsSingle(
                signatures.Where(static type => type.UnsupportedKind == "by-reference"));
            Assert.AreEqual(PrimitiveTypeCode.String, alias.PrimitiveType);
            ManagedMetadataTypeSignature target = Assert.ContainsSingle(
                signatures.Where(static type => type.MetadataName == "System.Exception"));
            Assert.IsNull(target.UnsupportedKind);
            Assert.IsNull(target.PrimitiveType);
            Assert.AreEqual(0x23000000U, target.AssemblyReferenceToken & 0xff000000U);
        });
    }

    /// <summary>
    /// Preserves intrinsic types and nested vector and multidimensional array shapes.
    /// </summary>
    [TestMethod]
    public void IntrinsicAndArraySignaturesRetainTheirShapes()
    {
        WithMetadata("""
            public static class Container
            {
                public static object Run(string[,][] text, System.Exception[] errors) => text;
            }
            """, (reader, _) =>
        {
            MethodSignature<ManagedMetadataTypeSignature> signature = FindMethod(reader, "Run")
                .DecodeSignature(ManagedMetadataTypeSignatureProvider.Instance, genericContext: null);
            Assert.AreEqual(PrimitiveTypeCode.Object, signature.ReturnType.PrimitiveType);
            Assert.AreEqual("System.Object", signature.ReturnType.MetadataName);
            Assert.IsFalse(signature.ReturnType.IsValueType);
            ManagedMetadataTypeSignature text = signature.ParameterTypes[0];
            Assert.AreEqual(PrimitiveTypeCode.String, text.PrimitiveType);
            Assert.AreSequenceEqual(
                new ManagedMetadataArrayShape[] { new(1, IsVector: true), new(2, IsVector: false) },
                text.ArrayShapes);
            ManagedMetadataTypeSignature errors = signature.ParameterTypes[1];
            Assert.IsNull(errors.PrimitiveType);
            Assert.AreEqual("System.Exception", errors.MetadataName);
            Assert.AreEqual(new ManagedMetadataArrayShape(1, IsVector: true),
                Assert.ContainsSingle(errors.ArrayShapes));
        });
    }

    private void WithMetadata(string source, Action<MetadataReader, PEReader> inspect)
    {
        string path = Path.Join(Path.GetTempPath(), $"csls-metadata-signature-{Guid.NewGuid():N}.dll");
        try
        {
            string assemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
                ?? throw new AssertFailedException("The runtime assembly list is unavailable.");
            var compilation = CSharpCompilation.Create(
                "SignatureFixture",
                [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.CancellationToken)],
                assemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Select(static assembly => MetadataReference.CreateFromFile(assembly)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Debug, deterministic: true));
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                EmitResult result = compilation.Emit(output, cancellationToken: TestContext.CancellationToken);
                Assert.IsTrue(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            }

            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var pe = new PEReader(input);
            inspect(pe.GetMetadataReader(), pe);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static MethodDefinition FindMethod(MetadataReader reader, string name) =>
        reader.GetMethodDefinition(Assert.ContainsSingle(reader.MethodDefinitions.Where(
            handle => reader.GetString(reader.GetMethodDefinition(handle).Name) == name)));
}
