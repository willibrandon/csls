using Csls.Debugger.Contracts;
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
/// Emits a real executable and matching Roslyn Hot Reload generation for runtime tests.
/// </summary>
internal static partial class HotReloadTestCompilation
{
    private const string AssemblyName = "CslsHotReloadTarget";

    /// <summary>
    /// Emits the baseline executable, symbols, source, and first compiler delta generation.
    /// </summary>
    /// <param name="directory">The isolated output directory.</param>
    /// <param name="cancellationToken">Cancels compilation and file writes.</param>
    /// <param name="updateLocalDeclarations">Whether the new body introduces reference local declarations.</param>
    /// <param name="addMethod">Whether the update adds a method with reference arguments.</param>
    /// <returns>The executable paths, updated source, breakpoint line, and compiler deltas.</returns>
    internal static async Task<(
        string ProgramPath,
        string SourcePath,
        string UpdatedSource,
        int BreakpointLine,
        int UpdatedValueLine,
        byte[] MetadataDelta,
        byte[] IlDelta,
        byte[] PdbDelta,
        int[] UpdatedTypes,
        int[] UpdatedMethods)> EmitAsync(
            string directory,
            CancellationToken cancellationToken,
            bool updateLocalDeclarations = false,
            bool addMethod = false)
    {
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Join(directory, "Program.cs");
        string programPath = Path.Join(directory, $"{AssemblyName}.dll");
        string pdbPath = Path.ChangeExtension(programPath, ".pdb");
        string baselineSource = CreateSource(1);
        string updatedSource = CreateSource(2, updateLocalDeclarations, addMethod);
        CSharpCompilation baselineCompilation = CreateCompilation(
            baselineSource,
            sourcePath);
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        EmitResult baselineResult = baselineCompilation.Emit(
            pe,
            pdb,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
            cancellationToken: cancellationToken);
        if (!baselineResult.Success)
        {
            throw new InvalidOperationException(FormatDiagnostics(baselineResult.Diagnostics));
        }

        byte[] peImage = pe.ToArray();
        byte[] pdbImage = pdb.ToArray();
        using var module = ModuleMetadata.CreateFromImage(ImmutableArray.Create(peImage));
        using var peReader = new PEReader(new MemoryStream(peImage, writable: false));
        MetadataReader metadata = peReader.GetMetadataReader();
        var baseline = EmitBaseline.CreateInitialBaseline(
            baselineCompilation,
            module,
            debugInformationProvider: static _ => default,
            localSignatureProvider: method => GetLocalSignature(metadata, peReader, method),
            hasPortableDebugInformation: true);
        CSharpCompilation updatedCompilation = CreateCompilation(updatedSource, sourcePath);
        var edit = new SemanticEdit(
            SemanticEditKind.Update,
            FindValueMethod(baselineCompilation),
            FindValueMethod(updatedCompilation));
        List<SemanticEdit> edits = [edit];
        IMethodSymbol? addedMethod = null;
        if (addMethod)
        {
            addedMethod = updatedCompilation.GetTypeByMetadataName("Program")?.GetMembers("Added")
                .OfType<IMethodSymbol>().Single()
                ?? throw new InvalidOperationException("The added method was not compiled.");
            edits.Add(new SemanticEdit(SemanticEditKind.Insert, oldSymbol: null, addedMethod));
        }
        using var metadataDelta = new MemoryStream();
        using var ilDelta = new MemoryStream();
        using var pdbDelta = new MemoryStream();
        EmitDifferenceResult updateResult = updatedCompilation.EmitDifference(
            baseline,
            edits,
            isAddedSymbol: symbol => SymbolEqualityComparer.Default.Equals(symbol, addedMethod),
            metadataDelta,
            ilDelta,
            pdbDelta,
            cancellationToken);
        if (!updateResult.Success)
        {
            throw new InvalidOperationException(FormatDiagnostics(updateResult.Diagnostics));
        }

        await File.WriteAllTextAsync(
            sourcePath,
            baselineSource,
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(programPath, peImage, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(pdbPath, pdbImage, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(directory, $"{AssemblyName}.runtimeconfig.json"),
            CreateRuntimeConfiguration(),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        int breakpointLine = baselineSource
            .Split('\n')
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(static item => item.Line.Contains("Thread.Sleep(10);", StringComparison.Ordinal))
            .Number;
        int updatedValueLine = updatedSource
            .Split('\n')
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(static item => item.Line.Contains(
                "private static int Value()",
                StringComparison.Ordinal))
            .Number;
        return (
            programPath,
            sourcePath,
            updatedSource,
            breakpointLine,
            updatedValueLine,
            metadataDelta.ToArray(),
            ilDelta.ToArray(),
            pdbDelta.ToArray(),
            [.. updateResult.ChangedTypes.Select(static handle => MetadataTokens.GetToken(handle))],
            [.. updateResult.UpdatedMethods.Select(static handle => MetadataTokens.GetToken(handle))]);
    }

    /// <summary>
    /// Emits a target whose active method continuation changes across one compiler generation.
    /// </summary>
    /// <param name="directory">The isolated output directory.</param>
    /// <param name="cancellationToken">Cancels compilation and file writes.</param>
    /// <returns>The executable, active source location, compiler remap, and deltas.</returns>
    internal static async Task<(
        string ProgramPath,
        string SourcePath,
        string UpdatedSource,
        int BreakpointLine,
        DebugHotReloadActiveStatement ActiveStatement,
        byte[] MetadataDelta,
        byte[] IlDelta,
        byte[] PdbDelta,
        int[] UpdatedTypes,
        int[] UpdatedMethods)> EmitActiveMethodAsync(
            string directory,
            CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Join(directory, "Program.cs");
        string programPath = Path.Join(directory, $"{AssemblyName}.dll");
        string baselineSource = CreateActiveSource(1);
        string updatedSource = CreateActiveSource(10);
        CSharpCompilation baselineCompilation = CreateCompilation(baselineSource, sourcePath);
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        EmitResult baselineResult = baselineCompilation.Emit(
            pe,
            pdb,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
            cancellationToken: cancellationToken);
        if (!baselineResult.Success)
        {
            throw new InvalidOperationException(FormatDiagnostics(baselineResult.Diagnostics));
        }

        byte[] peImage = pe.ToArray();
        byte[] pdbImage = pdb.ToArray();
        using var module = ModuleMetadata.CreateFromImage(ImmutableArray.Create(peImage));
        using var peReader = new PEReader(new MemoryStream(peImage, writable: false));
        MetadataReader metadata = peReader.GetMetadataReader();
        MethodDefinitionHandle methodHandle = metadata.MethodDefinitions.Single(handle =>
            metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "Value");
        uint methodToken = checked((uint)MetadataTokens.GetToken(methodHandle));
        var baseline = EmitBaseline.CreateInitialBaseline(
            baselineCompilation,
            module,
            debugInformationProvider: static _ => default,
            localSignatureProvider: method => GetLocalSignature(metadata, peReader, method),
            hasPortableDebugInformation: true);
        CSharpCompilation updatedCompilation = CreateCompilation(updatedSource, sourcePath);
        var edit = new SemanticEdit(
            SemanticEditKind.Update,
            FindValueMethod(baselineCompilation),
            FindValueMethod(updatedCompilation));
        using var metadataDelta = new MemoryStream();
        using var ilDelta = new MemoryStream();
        using var pdbDelta = new MemoryStream();
        EmitDifferenceResult updateResult = updatedCompilation.EmitDifference(
            baseline,
            [edit],
            isAddedSymbol: static _ => false,
            metadataDelta,
            ilDelta,
            pdbDelta,
            cancellationToken);
        if (!updateResult.Success)
        {
            throw new InvalidOperationException(FormatDiagnostics(updateResult.Diagnostics));
        }

        byte[] pdbDeltaImage = pdbDelta.ToArray();
        int breakpointLine = baselineSource
            .Split('\n')
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(static item => item.Line.Contains(
                "Thread.Sleep(1);",
                StringComparison.Ordinal))
            .Number;
        using DebugSymbolReader baselineSymbols = DebugSymbolReader.TryOpen(pdbImage)
            ?? throw new InvalidOperationException("The active-method baseline PDB is unreadable.");
        ManagedSequencePoint oldPoint = baselineSymbols.GetSequencePoints(methodToken)
            .Single(point => point.StartLine == breakpointLine);
        using DebugSymbolReader updatedSymbols = DebugSymbolReader.TryOpen(
            pdbImage,
            [pdbDeltaImage])
            ?? throw new InvalidOperationException("The active-method delta PDB is unreadable.");
        ManagedSequencePoint newPoint = updatedSymbols.GetSequencePoints(methodToken)
            .Single(point => point.StartLine == breakpointLine);
        var activeStatement = new DebugHotReloadActiveStatement(
            methodToken,
            MethodVersion: 1,
            checked((uint)oldPoint.IlOffset),
            newPoint.StartLine - 1,
            newPoint.StartColumn - 1,
            newPoint.EndLine - 1,
            newPoint.EndColumn - 1);

        await File.WriteAllTextAsync(
            sourcePath,
            baselineSource,
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(programPath, peImage, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(
            Path.ChangeExtension(programPath, ".pdb"),
            pdbImage,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(directory, $"{AssemblyName}.runtimeconfig.json"),
            CreateRuntimeConfiguration(),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        return (
            programPath,
            sourcePath,
            updatedSource,
            breakpointLine,
            activeStatement,
            metadataDelta.ToArray(),
            ilDelta.ToArray(),
            pdbDeltaImage,
            [.. updateResult.ChangedTypes.Select(static handle => MetadataTokens.GetToken(handle))],
            [.. updateResult.UpdatedMethods.Select(static handle => MetadataTokens.GetToken(handle))]);
    }

    private static CSharpCompilation CreateCompilation(string source, string sourcePath) =>
        CSharpCompilation.Create(
            AssemblyName,
            [CSharpSyntaxTree.ParseText(
                Microsoft.CodeAnalysis.Text.SourceText.From(
                    source,
                    Encoding.UTF8,
                    Microsoft.CodeAnalysis.Text.SourceHashAlgorithm.Sha256),
                new CSharpParseOptions(LanguageVersion.Preview),
                sourcePath)],
            GetPlatformReferences(),
            new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                optimizationLevel: OptimizationLevel.Debug,
                deterministic: true));

    private static ImmutableArray<MetadataReference> GetPlatformReferences()
    {
        string assemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException(
                "The trusted platform assembly list is unavailable.");
        return [.. assemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(static path => MetadataReference.CreateFromFile(path))];
    }

    private static IMethodSymbol FindValueMethod(CSharpCompilation compilation) =>
        compilation.GetTypeByMetadataName("Program")?
            .GetMembers("Value")
            .OfType<IMethodSymbol>()
            .Single()
        ?? throw new InvalidOperationException("The Hot Reload target method was not compiled.");

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

    private static string CreateRuntimeConfiguration() => $$"""
        {
          "runtimeOptions": {
            "tfm": "net{{Environment.Version.Major}}.0",
            "framework": {
              "name": "Microsoft.NETCore.App",
              "version": "{{Environment.Version.ToString(3)}}"
            }
          }
        }
        """;

    private static string CreateSource(int value, bool referenceLocals = false, bool addMethod = false) => $$"""
        using System;
        using System.IO;
        using System.Threading;

        internal static class Program
        {
            private static int Value() {{(referenceLocals ? $$"""
            {
                Exception target = new InvalidOperationException("original");
                var source = new ArgumentException("replacement");
                GC.KeepAlive(source);
                GC.KeepAlive(target);
                return {{value}};
            }
            """ : addMethod ? "=> Added(new InvalidOperationException(\"original\"), new ArgumentException(\"replacement\"), (11, 12));" : $"=> {value};")}}

            {{(addMethod ? $$"""
            private static int Added(Exception target, ArgumentException source, (int first, int second) pair)
            {
                GC.KeepAlive(source);
                GC.KeepAlive(target);
                return {{value}};
            }
            """ : "")}}

            private static void Main(string[] arguments)
            {
                while (!File.Exists(arguments[0]))
                {
                    Thread.Sleep(10);
                }

                Console.Write(Value());
            }
        }
        """;

    private static string CreateActiveSource(int increment) => $$"""
        using System;
        using System.Threading;

        internal static class Program
        {
            private static int Value()
            {
                int value = 1;
                Thread.Sleep(1);
                return value + {{increment}};
            }

            private static void Main()
            {
                Console.Write(Value());
            }
        }
        """;

    private static string FormatDiagnostics(ImmutableArray<Diagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(static item => item.ToString()));
}
