using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies Portable PDB generation chains produced by the real C# compiler.
/// </summary>
[TestClass]
public sealed class DebugSymbolReaderHotReloadTests
{
    private const string SourcePath = "/workspace/Calculator.cs";

    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Reads current sequence points and locals from the newest symbol generation.
    /// </summary>
    [TestMethod]
    public void CompilerProducedDeltasUseNewestMethodSymbols()
    {
        const string source0 = """
            namespace Sample;
            internal static class Calculator
            {
                internal static int Value()
                {
                    int original = 1;
                    return original;
                }
            }
            """;
        const string source1 = """
            namespace Sample;
            internal static class Calculator
            {
                internal static int Value()
                {

                    int replacement = 2;
                    return replacement;
                }
            }
            """;
        const string source2 = """
            namespace Sample;
            internal static class Calculator
            {
                internal static int Value()
                {


                    int finalValue = 3;
                    return finalValue;
                }
            }
            """;

        CSharpCompilation compilation0 = CreateCompilation(source0);
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        EmitResult initialResult = compilation0.Emit(
            pe,
            pdb,
            options: new EmitOptions(
                debugInformationFormat: DebugInformationFormat.PortablePdb,
                pdbFilePath: "Calculator.pdb"),
            cancellationToken: TestContext.CancellationToken);
        Assert.IsTrue(initialResult.Success, FormatDiagnostics(initialResult.Diagnostics));

        byte[] peImage = pe.ToArray();
        byte[] pdbImage = pdb.ToArray();
        using var module = ModuleMetadata.CreateFromImage(
            ImmutableArray.Create(peImage));
        using var peReader = new PEReader(new MemoryStream(peImage, writable: false));
        MetadataReader metadata = peReader.GetMetadataReader();
        uint methodToken = FindMethodToken(metadata, "Value");
        var baseline = EmitBaseline.CreateInitialBaseline(
            compilation0,
            module,
            debugInformationProvider: static _ => default,
            localSignatureProvider: method => GetLocalSignature(metadata, peReader, method),
            hasPortableDebugInformation: true);

        CSharpCompilation compilation1 = CreateCompilation(source1);
        (EmitDifferenceResult result1, byte[] metadataDelta1, byte[] ilDelta1, byte[] pdbDelta1) = EmitUpdate(
            compilation0,
            compilation1,
            baseline,
            TestContext.CancellationToken);
        Assert.IsTrue(result1.Success, FormatDiagnostics(result1.Diagnostics));
        EmitBaseline generation1Baseline = result1.Baseline
            ?? throw new AssertFailedException("The first compiler delta did not produce a baseline.");

        CSharpCompilation compilation2 = CreateCompilation(source2);
        (EmitDifferenceResult result2, byte[] metadataDelta2, byte[] ilDelta2, byte[] pdbDelta2) = EmitUpdate(
            compilation1,
            compilation2,
            generation1Baseline,
            TestContext.CancellationToken);
        Assert.IsTrue(result2.Success, FormatDiagnostics(result2.Diagnostics));

        var loadedModule = new CorDebugLoadedModule
        {
            Id = 1,
            Path = null,
            Pointer = 1,
            Identity = 1,
            ModuleImage = peImage,
            SymbolImage = pdbImage,
            HotReloadCapabilities = ["Baseline"]
        };
        NotSupportedException unsupported = Assert.Throws<NotSupportedException>(() =>
            HotReloadDeltaValidator.Validate(
                loadedModule,
                metadataDelta1,
                ilDelta1,
                pdbDelta1,
                [.. result1.ChangedTypes.Select(static handle => MetadataTokens.GetToken(handle))],
                ["FutureRuntimeCapability"],
                [.. result1.UpdatedMethods.Select(static handle => MetadataTokens.GetToken(handle))],
                []));
        Assert.Contains("FutureRuntimeCapability", unsupported.Message, StringComparison.Ordinal);
        IReadOnlyList<uint> generation1Methods = HotReloadDeltaValidator.Validate(
            loadedModule,
            metadataDelta1,
            ilDelta1,
            pdbDelta1,
            [.. result1.ChangedTypes.Select(static handle => MetadataTokens.GetToken(handle))],
            ["Baseline"],
            [.. result1.UpdatedMethods.Select(static handle => MetadataTokens.GetToken(handle))],
            []).UpdatedMethods;
        Assert.Contains(methodToken, generation1Methods);
        loadedModule.MetadataDeltas.Add(metadataDelta1);
        loadedModule.SymbolDeltas.Add(pdbDelta1);
        loadedModule.HotReloadGeneration = 1;
        IReadOnlyList<uint> generation2Methods = HotReloadDeltaValidator.Validate(
            loadedModule,
            metadataDelta2,
            ilDelta2,
            pdbDelta2,
            [.. result2.ChangedTypes.Select(static handle => MetadataTokens.GetToken(handle))],
            ["Baseline"],
            [.. result2.UpdatedMethods.Select(static handle => MetadataTokens.GetToken(handle))],
            []).UpdatedMethods;
        Assert.Contains(methodToken, generation2Methods);

        using DebugSymbolReader baseSymbols = DebugSymbolReader.TryOpen(pdbImage)
            ?? throw new AssertFailedException("The baseline Portable PDB was not readable.");
        ManagedSequencePoint basePoint = baseSymbols.GetSequencePoints(methodToken)
            .Single(point => point.StartLine == 6);
        Assert.AreEqual(SourcePath, basePoint.SourcePath);
        Assert.Contains(
            "original",
            baseSymbols.GetLocalVariables(
                    methodToken,
                    checked((uint)basePoint.IlOffset))
                .Values
                .Select(static variable => variable.Name));

        using DebugSymbolReader generation1Symbols = DebugSymbolReader.TryOpen(
            pdbImage,
            [pdbDelta1])
            ?? throw new AssertFailedException("The first Portable PDB delta was not readable.");
        ManagedSequencePoint generation1Point = generation1Symbols.GetSequencePoints(methodToken)
            .Single(point => point.StartLine == 7);
        Assert.AreEqual(SourcePath, generation1Point.SourcePath);
        Assert.Contains(
            "replacement",
            generation1Symbols.GetLocalVariables(
                    methodToken,
                    checked((uint)generation1Point.IlOffset))
                .Values
                .Select(static variable => variable.Name));

        using DebugSymbolReader generation2Symbols = DebugSymbolReader.TryOpen(
            pdbImage,
            [pdbDelta1, pdbDelta2])
            ?? throw new AssertFailedException("The second Portable PDB delta was not readable.");
        ManagedSequencePoint generation2Point = generation2Symbols.GetSequencePoints(methodToken)
            .Single(point => point.StartLine == 8);
        Assert.AreEqual(SourcePath, generation2Point.SourcePath);
        Assert.Contains(
            "finalValue",
            generation2Symbols.GetLocalVariables(
                    methodToken,
                    checked((uint)generation2Point.IlOffset))
                .Values
                .Select(static variable => variable.Name));
        ManagedSymbolDocument currentDocument = generation2Symbols.GetDocuments()
            .Single(document => document.Path == SourcePath);
        Assert.IsNotNull(currentDocument.Checksum);
        Assert.AreEqual("SHA256", currentDocument.Checksum.Algorithm);
        string currentChecksum = GetSourceChecksum(compilation2);
        Assert.AreEqual(
            currentChecksum,
            currentDocument.Checksum.Value);
    }

    /// <summary>
    /// Rejects ordinary Portable PDB images supplied as Hot Reload generations.
    /// </summary>
    [TestMethod]
    public void FullPdbAsDeltaIsRejected()
    {
        CSharpCompilation compilation = CreateCompilation("""
            internal static class Calculator
            {
                internal static int Value() => 1;
            }
            """);
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        EmitResult result = compilation.Emit(
            pe,
            pdb,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
            cancellationToken: TestContext.CancellationToken);
        Assert.IsTrue(result.Success, FormatDiagnostics(result.Diagnostics));
        byte[] pdbImage = pdb.ToArray();

        BadImageFormatException exception = Assert.Throws<BadImageFormatException>(
            () => DebugSymbolReader.TryOpen(pdbImage, [pdbImage]));
        Assert.Contains(
            "valid minimal Portable PDB delta",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads compiler tuple transforms including nested and Rest storage segments.
    /// </summary>
    [TestMethod]
    public void CompilerProducedTupleTransformsPreserveStorageShape()
    {
        CSharpCompilation compilation = CreateCompilation("""
            using System;

            internal static class Calculator
            {
                internal static int Value()
                {
                    (int One, int Two, int Three, int Four, int Five, int Six, int Seven,
                        int Eight) eight = (1, 2, 3, 4, 5, 6, 7, 8);
                    (int One, int Two, int Three, int Four, int Five, int Six, int Seven,
                        int Eight, int Nine) nine = (1, 2, 3, 4, 5, 6, 7, 8, 9);
                    ((int X, int Y) Point, string Label) nested = ((1, 2), "point");
                    (int Left, int Right)[] array = [(1, 2)];
                    GC.KeepAlive(eight);
                    GC.KeepAlive(nine);
                    GC.KeepAlive(nested);
                    GC.KeepAlive(array);
                    return eight.One + nine.Nine + nested.Point.X + array[0].Left;
                }
            }
            """);
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        EmitResult result = compilation.Emit(
            pe,
            pdb,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
            cancellationToken: TestContext.CancellationToken);
        Assert.IsTrue(result.Success, FormatDiagnostics(result.Diagnostics));

        byte[] peImage = pe.ToArray();
        using var peReader = new PEReader(new MemoryStream(peImage, writable: false));
        uint methodToken = FindMethodToken(peReader.GetMetadataReader(), "Value");
        using DebugSymbolReader symbols = DebugSymbolReader.TryOpen(pdb.ToArray())
            ?? throw new AssertFailedException("The compiler Portable PDB was not readable.");
        uint ilOffset = checked((uint)symbols.GetSequencePoints(methodToken).Max(
            static point => point.IlOffset));
        var locals = symbols
            .GetLocalVariables(methodToken, ilOffset)
            .Values
            .ToDictionary(static variable => variable.Name, StringComparer.Ordinal);

        AssertTupleTransforms(
            locals["eight"],
            ["One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", null]);
        AssertTupleTransforms(
            locals["nine"],
            ["One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", null, null]);
        AssertTupleTransforms(locals["nested"], ["Point", "Label", "X", "Y"]);
        AssertTupleTransforms(locals["array"], ["Left", "Right"]);
    }

    /// <summary>
    /// Rejects malformed or unbounded Portable PDB tuple transforms.
    /// </summary>
    [TestMethod]
    public void MalformedTupleTransformsAreIgnored()
    {
        Assert.IsNull(ManagedTupleElementNameReader.DecodePortablePdb([]));
        Assert.IsNull(ManagedTupleElementNameReader.DecodePortablePdb([1]));
        Assert.IsNull(ManagedTupleElementNameReader.DecodePortablePdb([0]));
        Assert.IsNull(ManagedTupleElementNameReader.DecodePortablePdb([0xc3, 0x28, 0]));
        Assert.IsNull(ManagedTupleElementNameReader.DecodePortablePdb(
            new byte[(64 * 1024) + 1]));
        Assert.IsNull(ManagedTupleElementNameReader.DecodePortablePdb(
            [.. Enumerable.Repeat((byte)'a', (16 * 1024) + 1), 0]));
        Assert.IsNull(ManagedTupleElementNameReader.DecodePortablePdb(
            new byte[(4 * 1024 * 1024) + 1]));
    }

    private static CSharpCompilation CreateCompilation(string source) =>
        CSharpCompilation.Create(
            "HotReloadFixture",
            [CSharpSyntaxTree.ParseText(
                Microsoft.CodeAnalysis.Text.SourceText.From(
                    source,
                    Encoding.UTF8,
                    Microsoft.CodeAnalysis.Text.SourceHashAlgorithm.Sha256),
                new CSharpParseOptions(LanguageVersion.Preview),
                SourcePath)],
            GetPlatformReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                deterministic: true));

    private static ImmutableArray<MetadataReference> GetPlatformReferences()
    {
        string assemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new AssertFailedException("The trusted platform assembly list is unavailable.");
        return [.. assemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(static path => MetadataReference.CreateFromFile(path))];
    }

    private static (
        EmitDifferenceResult Result,
        byte[] MetadataDelta,
        byte[] IlDelta,
        byte[] PdbDelta) EmitUpdate(
        CSharpCompilation oldCompilation,
        CSharpCompilation newCompilation,
        EmitBaseline baseline,
        CancellationToken cancellationToken)
    {
        IMethodSymbol oldMethod = FindMethod(oldCompilation);
        IMethodSymbol newMethod = FindMethod(newCompilation);
        var edit = new SemanticEdit(
            SemanticEditKind.Update,
            oldMethod,
            newMethod);
        using var metadataDelta = new MemoryStream();
        using var ilDelta = new MemoryStream();
        using var pdbDelta = new MemoryStream();
        EmitDifferenceResult result = newCompilation.EmitDifference(
            baseline,
            [edit],
            isAddedSymbol: static _ => false,
            metadataDelta,
            ilDelta,
            pdbDelta,
            cancellationToken);
        return (
            result,
            metadataDelta.ToArray(),
            ilDelta.ToArray(),
            pdbDelta.ToArray());
    }

    private static IMethodSymbol FindMethod(CSharpCompilation compilation) =>
        compilation.GetTypeByMetadataName("Sample.Calculator")?
            .GetMembers("Value")
            .OfType<IMethodSymbol>()
            .Single()
        ?? compilation.GetTypeByMetadataName("Calculator")?
            .GetMembers("Value")
            .OfType<IMethodSymbol>()
            .Single()
        ?? throw new AssertFailedException("The fixture method was not compiled.");

    private static uint FindMethodToken(MetadataReader metadata, string name)
    {
        MethodDefinitionHandle handle = metadata.MethodDefinitions.FirstOrDefault(
            handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name) == name);
        if (!handle.IsNil)
        {
            return checked((uint)MetadataTokens.GetToken(handle));
        }

        throw new AssertFailedException($"Method '{name}' was not emitted.");
    }

    private static StandaloneSignatureHandle GetLocalSignature(
        MetadataReader metadata,
        PEReader peReader,
        MethodDefinitionHandle method)
    {
        int relativeVirtualAddress = metadata.GetMethodDefinition(method).RelativeVirtualAddress;
        return relativeVirtualAddress == 0
            ? default
            : peReader.GetMethodBody(relativeVirtualAddress).LocalSignature;
    }

    private static string FormatDiagnostics(ImmutableArray<Diagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.ToString()));

    private static void AssertTupleTransforms(
        ManagedSymbolVariable variable,
        IReadOnlyList<string?> expected)
    {
        ManagedTupleCustomTypeInfo info = variable.TupleCustomTypeInfo
            ?? throw new AssertFailedException(
                $"Local '{variable.Name}' did not retain tuple transforms.");
        Assert.AreSequenceEqual(expected, info.TransformNames);
    }

    private string GetSourceChecksum(CSharpCompilation compilation) =>
        Convert.ToHexString(compilation.SyntaxTrees.Single()
            .GetText(TestContext.CancellationToken)
            .GetChecksum()
            .AsSpan());
}
