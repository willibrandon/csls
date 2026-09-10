using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Runs an analyzer against an actual file-backed Roslyn compilation and reports compiler failures.
/// </summary>
internal static class CodeQlFileCompilation
{
    /// <summary>
    /// Writes a scoped source fixture, verifies its compilation, analyzes it, and releases the file.
    /// </summary>
    internal static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string source, DiagnosticAnalyzer analyzer, CancellationToken cancellationToken)
    {
        string path = Path.Join(Path.GetTempPath(), $"csls-codeql-input-{Guid.NewGuid():N}.cs");
        try
        {
            await File.WriteAllTextAsync(path, source, cancellationToken).ConfigureAwait(false);
            SyntaxTree tree;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                tree = CSharpSyntaxTree.ParseText(SourceText.From(stream, Encoding.UTF8),
                    new CSharpParseOptions(LanguageVersion.CSharp14), path, cancellationToken);
            }

            string trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
                ?? throw new InvalidOperationException("The runtime did not expose trusted platform assemblies.");
            IEnumerable<MetadataReference> references = trusted.Split(Path.PathSeparator)
                .Select(static assembly => MetadataReference.CreateFromFile(assembly));
            var compilation = CSharpCompilation.Create("AnalyzerInput", [tree], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.IsEmpty(compilation.GetDiagnostics(cancellationToken)
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
            var options = new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), onAnalyzerException: null,
                concurrentAnalysis: true, logAnalyzerExecutionTime: true, reportSuppressedDiagnostics: false);
            return await compilation.WithAnalyzers([analyzer], options).GetAnalyzerDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
