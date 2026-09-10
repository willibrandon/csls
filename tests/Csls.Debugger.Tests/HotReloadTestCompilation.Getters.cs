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
/// Emits getter replacements that change field identity, introduce a return local, and then mutate target storage.
/// </summary>
internal static partial class HotReloadTestCompilation
{
    /// <summary>
    /// Creates a target with two real compiler updates to one existing getter.
    /// </summary>
    internal static async Task<(string Program, string Source, int Line, IReadOnlyList<HotReloadDeclarationUpdate> Updates)>
        EmitGetterGenerationsAsync(string directory, CancellationToken cancellationToken)
    {
        string sourcePath = Path.Join(directory, "Program.cs");
        string programPath = Path.Join(directory, $"{AssemblyName}.dll");
        string source = CreateGetterSource(0);
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
            string updatedSource = CreateGetterSource(generation);
            CSharpCompilation replacement = CreateCompilation(updatedSource, sourcePath);
            var edit = new SemanticEdit(SemanticEditKind.Update, FindGetter(compilation), FindGetter(replacement));
            using var metadataDelta = new MemoryStream();
            using var ilDelta = new MemoryStream();
            using var pdbDelta = new MemoryStream();
            EmitDifferenceResult result = replacement.EmitDifference(baseline, [edit], isAddedSymbol: static _ => false,
                metadataDelta, ilDelta, pdbDelta, cancellationToken);
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
        await File.WriteAllBytesAsync(Path.ChangeExtension(programPath, ".pdb"), pdb.ToArray(), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Join(directory, $"{AssemblyName}.runtimeconfig.json"),
            CreateRuntimeConfiguration(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(sourcePath, source, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        int line = source.Split('\n').Select(static (text, index) => (Text: text, Line: index + 1))
            .Single(static item => item.Text.Contains("Console.Write(receiver.Value);", StringComparison.Ordinal)).Line;
        return (programPath, sourcePath, line, updates);
    }

    private static IMethodSymbol FindGetter(CSharpCompilation compilation) =>
        compilation.GetTypeByMetadataName("Receiver")?.GetMembers("Value").OfType<IPropertySymbol>().Single().GetMethod
        ?? throw new InvalidOperationException("The target getter was not compiled.");

    private static string CreateGetterSource(int generation) => $$"""
        using System;

        internal static class Program
        {
            private static void Main()
            {
                var receiver = new Receiver();
                Console.Write(receiver.Value);
                GC.KeepAlive(receiver);
            }
        }

        internal sealed class Receiver
        {
            internal readonly int _first = 10;
            internal int _second = 42;
            internal int Value {{(generation switch
    {
        0 => "=> _first;",
        1 => "{ get { return _second; } }",
        _ => "=> ++_second;"
    })}}
        }
        """;
}
