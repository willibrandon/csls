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
/// Emits added and subsequently updated static methods, instance methods, and constructors.
/// </summary>
internal static partial class HotReloadTestCompilation
{
    /// <summary>
    /// Creates a real target and two compiler generations whose added members can be explicitly evaluated.
    /// </summary>
    internal static async Task<(string Program, string Source, int Line, IReadOnlyList<HotReloadDeclarationUpdate> Updates)>
        EmitCallableGenerationsAsync(string directory, CancellationToken cancellationToken)
    {
        string sourcePath = Path.Join(directory, "Program.cs");
        string programPath = Path.Join(directory, $"{AssemblyName}.dll");
        string source = CreateCallableSource(0);
        CSharpCompilation compilation = CreateCompilation(source, sourcePath);
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        EmitResult emitted = compilation.Emit(pe, pdb,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
            cancellationToken: cancellationToken);
        if (!emitted.Success)
        {
            throw new InvalidOperationException(FormatDiagnostics(emitted.Diagnostics));
        }

        byte[] peImage = pe.ToArray();
        using var module = ModuleMetadata.CreateFromImage(ImmutableArray.Create(peImage));
        using var reader = new PEReader(new MemoryStream(peImage, writable: false));
        MetadataReader metadata = reader.GetMetadataReader();
        var baseline = EmitBaseline.CreateInitialBaseline(compilation, module,
            debugInformationProvider: static _ => default,
            localSignatureProvider: method => GetLocalSignature(metadata, reader, method),
            hasPortableDebugInformation: true);
        List<HotReloadDeclarationUpdate> updates = [];
        for (int generation = 1; generation <= 2; generation++)
        {
            string updatedSource = CreateCallableSource(generation);
            CSharpCompilation replacement = CreateCompilation(updatedSource, sourcePath);
            IMethodSymbol[] currentMethods = FindCallableMethods(replacement);
            IMethodSymbol[] oldMethods = generation == 1 ? [] : FindCallableMethods(compilation);
            SemanticEdit[] edits = [.. currentMethods.Select((method, index) => generation == 1
                ? new SemanticEdit(SemanticEditKind.Insert, oldSymbol: null, method)
                : new SemanticEdit(SemanticEditKind.Update, oldMethods[index], method))];
            using var metadataDelta = new MemoryStream();
            using var ilDelta = new MemoryStream();
            using var pdbDelta = new MemoryStream();
            EmitDifferenceResult result = replacement.EmitDifference(baseline, edits,
                isAddedSymbol: symbol => generation == 1 && currentMethods.Any(method =>
                    SymbolEqualityComparer.Default.Equals(method, symbol)), metadataDelta, ilDelta, pdbDelta, cancellationToken);
            if (!result.Success)
            {
                throw new InvalidOperationException(FormatDiagnostics(result.Diagnostics));
            }

            updates.Add(new HotReloadDeclarationUpdate(updatedSource, metadataDelta.ToArray(), ilDelta.ToArray(),
                pdbDelta.ToArray(), [.. result.ChangedTypes.Select(static handle => MetadataTokens.GetToken(handle))],
                [.. result.UpdatedMethods.Select(static handle => MetadataTokens.GetToken(handle))]));
            baseline = result.Baseline ?? throw new InvalidOperationException("The emitted update has no next baseline.");
            compilation = replacement;
        }

        await File.WriteAllBytesAsync(programPath, peImage, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.ChangeExtension(programPath, ".pdb"), pdb.ToArray(), cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Join(directory, $"{AssemblyName}.runtimeconfig.json"),
            CreateRuntimeConfiguration(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(sourcePath, source, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        int line = source.Split('\n').Select(static (text, index) => (Text: text, Line: index + 1))
            .Single(static item => item.Text.Contains("GC.KeepAlive(receiver);", StringComparison.Ordinal)).Line;
        return (programPath, sourcePath, line, updates);
    }

    private static IMethodSymbol[] FindCallableMethods(CSharpCompilation compilation)
    {
        INamedTypeSymbol program = compilation.GetTypeByMetadataName("Program")
            ?? throw new InvalidOperationException("The callable target program was not compiled.");
        INamedTypeSymbol receiver = compilation.GetTypeByMetadataName("Receiver")
            ?? throw new InvalidOperationException("The callable target receiver was not compiled.");
        return
        [
            program.GetMembers("Added").OfType<IMethodSymbol>().Single(),
            receiver.GetMembers("Added").OfType<IMethodSymbol>().Single(),
            receiver.InstanceConstructors.Single(static method => method.Parameters.Length == 1 &&
                method.Parameters[0].Type.SpecialType == SpecialType.System_String)
        ];
    }

    private static string CreateCallableSource(int generation) => $$"""
        using System;

        internal static class Program
        {
            private static void Main()
            {
                Exception baseTarget = new InvalidOperationException("original");
                var derivedSource = new ArgumentException("replacement");
                var receiver = new Receiver(10);
                GC.KeepAlive(receiver);
                Console.Write(receiver.Value());
                GC.KeepAlive(baseTarget);
                GC.KeepAlive(derivedSource);
            }

            {{(generation == 0 ? "" : $$"""
            internal static Exception Added(ArgumentException value) => {{(generation == 1 ? "value" : "new ArgumentException(value.Message + \"-v2\")")}};
            """)}}
        }

        internal sealed class Receiver
        {
            internal readonly int _value;
            internal Receiver(int value) => _value = value;
            internal int Value() => _value;

            {{(generation == 0 ? "" : $$"""
            internal int Added(int offset) => _value + offset + {{generation}};
            internal Receiver(string value) => _value = int.Parse(value) + {{generation}};
            """)}}
        }
        """;
}
