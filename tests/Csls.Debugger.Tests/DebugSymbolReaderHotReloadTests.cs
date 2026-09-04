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
        (EmitDifferenceResult result1, byte[] pdbDelta1) = EmitUpdate(
            compilation0,
            compilation1,
            baseline,
            TestContext.CancellationToken);
        Assert.IsTrue(result1.Success, FormatDiagnostics(result1.Diagnostics));
        EmitBaseline generation1Baseline = result1.Baseline
            ?? throw new AssertFailedException("The first compiler delta did not produce a baseline.");

        CSharpCompilation compilation2 = CreateCompilation(source2);
        (EmitDifferenceResult result2, byte[] pdbDelta2) = EmitUpdate(
            compilation1,
            compilation2,
            generation1Baseline,
            TestContext.CancellationToken);
        Assert.IsTrue(result2.Success, FormatDiagnostics(result2.Diagnostics));

        using DebugSymbolReader baseSymbols = DebugSymbolReader.TryOpen(pdbImage)
            ?? throw new AssertFailedException("The baseline Portable PDB was not readable.");
        ManagedSequencePoint basePoint = baseSymbols.GetSequencePoints(methodToken)
            .Single(point => point.StartLine == 6);
        Assert.AreEqual(SourcePath, basePoint.SourcePath);
        Assert.Contains("original", baseSymbols.GetLocalNames(
            methodToken,
            checked((uint)basePoint.IlOffset)).Values);

        using DebugSymbolReader generation1Symbols = DebugSymbolReader.TryOpen(
            pdbImage,
            [pdbDelta1])
            ?? throw new AssertFailedException("The first Portable PDB delta was not readable.");
        ManagedSequencePoint generation1Point = generation1Symbols.GetSequencePoints(methodToken)
            .Single(point => point.StartLine == 7);
        Assert.AreEqual(SourcePath, generation1Point.SourcePath);
        Assert.Contains("replacement", generation1Symbols.GetLocalNames(
            methodToken,
            checked((uint)generation1Point.IlOffset)).Values);

        using DebugSymbolReader generation2Symbols = DebugSymbolReader.TryOpen(
            pdbImage,
            [pdbDelta1, pdbDelta2])
            ?? throw new AssertFailedException("The second Portable PDB delta was not readable.");
        ManagedSequencePoint generation2Point = generation2Symbols.GetSequencePoints(methodToken)
            .Single(point => point.StartLine == 8);
        Assert.AreEqual(SourcePath, generation2Point.SourcePath);
        Assert.Contains("finalValue", generation2Symbols.GetLocalNames(
            methodToken,
            checked((uint)generation2Point.IlOffset)).Values);
        ManagedSymbolDocument currentDocument = generation2Symbols.GetDocuments()
            .Single(document => document.Path == SourcePath);
        Assert.IsNotNull(currentDocument.Checksum);
        Assert.AreEqual("SHA256", currentDocument.Checksum.Algorithm);
        string baselineChecksum = GetSourceChecksum(compilation0);
        Assert.AreEqual(
            baselineChecksum,
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
        string? assemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        Assert.IsNotNull(assemblies);
        return [.. assemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(static path => MetadataReference.CreateFromFile(path))];
    }

    private static (EmitDifferenceResult Result, byte[] PdbDelta) EmitUpdate(
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
        return (result, pdbDelta.ToArray());
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
        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            if (metadata.GetString(metadata.GetMethodDefinition(handle).Name) == name)
            {
                return checked((uint)MetadataTokens.GetToken(handle));
            }
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

    private string GetSourceChecksum(CSharpCompilation compilation) =>
        Convert.ToHexString(compilation.SyntaxTrees.Single()
            .GetText(TestContext.CancellationToken)
            .GetChecksum()
            .AsSpan());
}
