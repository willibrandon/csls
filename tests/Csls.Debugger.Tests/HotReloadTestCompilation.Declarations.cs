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
/// Emits successive declaration changes with a target that enters each method generation.
/// </summary>
internal static partial class HotReloadTestCompilation
{
    /// <summary>
    /// Creates a baseline target and two real compiler generations with distinct local signatures.
    /// </summary>
    internal static async Task<(string Program, string Source, int EntryLine, IReadOnlyList<HotReloadDeclarationUpdate> Updates)>
        EmitDeclarationGenerationsAsync(string directory, CancellationToken cancellationToken, bool renameUpdatedLocals = false)
    {
        string sourcePath = Path.Join(directory, "Program.cs");
        string programPath = Path.Join(directory, $"{AssemblyName}.dll");
        string source = CreateDeclarationSource(0);
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
            string updatedSource = CreateDeclarationSource(generation);
            if (renameUpdatedLocals && generation == 2)
            {
                updatedSource = updatedSource.Replace("target", "currentTarget", StringComparison.Ordinal)
                    .Replace("source", "currentSource", StringComparison.Ordinal)
                    .Replace("object currentTarget", "\n\n\nobject currentTarget", StringComparison.Ordinal);
            }

            CSharpCompilation replacement = CreateCompilation(updatedSource, sourcePath);
            var edit = new SemanticEdit(SemanticEditKind.Update, FindValueMethod(compilation), FindValueMethod(replacement));
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
        await File.WriteAllBytesAsync(Path.ChangeExtension(programPath, ".pdb"), pdb.ToArray(), cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Join(directory, $"{AssemblyName}.runtimeconfig.json"),
            CreateRuntimeConfiguration(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(sourcePath, source, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        int entryLine = source.Split('\n').Select(static (text, index) => (Text: text, Line: index + 1))
            .Single(static item => item.Text.Contains("Console.Write(Value());", StringComparison.Ordinal)).Line;
        return (programPath, sourcePath, entryLine, updates);
    }

    private static string CreateDeclarationSource(int generation) => $$"""
        using System;

        internal static class Program
        {
            private static int Value()
            {
                {{(generation == 0 ? "" : $$"""
                {{(generation == 1 ? "ArgumentException target = new ArgumentException(\"original\");" : "object target = new InvalidOperationException(\"original\");")}}
                var source = {{(generation == 1 ? "new ArgumentException(\"replacement\")" : "new ArgumentNullException(\"parameter\", \"replacement\")")}};
                GC.KeepAlive(source);
                GC.KeepAlive(target);
                """)}}
                return {{generation}};
            }

            private static void Main()
            {
                for (int iteration = 0; iteration < 2; iteration++)
                {
                    Console.Write(Value());
                }
            }
        }
        """;
}
