using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies catch-all failures are detected through real file-backed Roslyn compilations.
/// </summary>
/// <param name="testContext">The running test's cooperative cancellation context.</param>
[TestClass]
public sealed class CodeQlGenericCatchAnalyzerTests(TestContext testContext)
{
    /// <summary>
    /// Rejects consumption, wrapping, and rethrows that belong to a different exception handler.
    /// </summary>
    /// <param name="handler">The unfiltered handler under analysis.</param>
    [TestMethod]
    [DataRow("catch { return; }")]
    [DataRow("catch (Exception) { Console.Error.WriteLine(\"Failed\"); }")]
    [DataRow("catch (global::System.Exception error) { throw new InvalidOperationException(\"Receiver failed\", error); }")]
    [DataRow("catch (Failure error) { throw new AggregateException(\"Both failed\", error); }")]
    [DataRow("catch (Exception error) { throw error; }")]
    [DataRow("catch (Exception) { try { Console.WriteLine(\"Cleanup\"); } catch (InvalidOperationException) { throw; } }")]
    [DataRow("catch (Exception) { static void Cleanup() { try { Console.WriteLine(\"Cleanup\"); } catch (InvalidOperationException) { throw; } } Cleanup(); }")]
    [DataRow("catch (Exception) { Action cleanup = () => { try { Console.WriteLine(\"Cleanup\"); } catch (InvalidOperationException) { throw; } }; cleanup(); }")]
    public async Task ReportsUnfilteredGenericCatch(string handler)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(handler).ConfigureAwait(false);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlGenericCatchAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.IsNotNull(diagnostic.Location.SourceTree);
        SourceText source = await diagnostic.Location.SourceTree.GetTextAsync(testContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("catch", source.ToString(diagnostic.Location.SourceSpan));
        Assert.AreEqual(source.ToString().IndexOf(handler, StringComparison.Ordinal), diagnostic.Location.SourceSpan.Start);
    }

    /// <summary>
    /// Accepts typed recovery, filtered translation, and cleanup that propagates its own exception.
    /// </summary>
    /// <param name="handler">The deliberately bounded or propagating handler.</param>
    [TestMethod]
    [DataRow("catch (InvalidOperationException error) { throw new AggregateException(error); }")]
    [DataRow("catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException) { throw new InvalidOperationException(\"Receiver failed\", error); }")]
    [DataRow("catch { Console.Error.WriteLine(\"Cleanup\"); throw; }")]
    [DataRow("catch (Failure) { if (stopped) { throw; } Console.Error.WriteLine(\"Still running\"); }")]
    [DataRow("catch (Exception) { try { Console.WriteLine(\"Cleanup\"); throw; } finally { Console.Error.WriteLine(\"Released\"); } }")]
    public async Task AcceptsNarrowedOrRethrownCatch(string handler)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(handler).ConfigureAwait(false);
        Assert.IsEmpty(diagnostics);
    }

    private async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string handler)
    {
        string source = $$"""
            using System;
            using System.IO;
            using Failure = System.Exception;
            internal static class ProgressReceiver
            {
                internal static void Report(bool stopped)
                {
                    try { Console.WriteLine("Progress"); }
                    {{handler}}
                }
            }
            """;
        string sourcePath = Path.Join(Path.GetTempPath(), $"csls-generic-catch-{Guid.NewGuid():N}.cs");
        try
        {
            await File.WriteAllTextAsync(sourcePath, source, testContext.CancellationToken).ConfigureAwait(false);
            SyntaxTree tree;
            using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                tree = CSharpSyntaxTree.ParseText(SourceText.From(stream, Encoding.UTF8),
                    new CSharpParseOptions(LanguageVersion.CSharp14), path: sourcePath,
                    cancellationToken: testContext.CancellationToken);
            }

            string trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
                ?? throw new InvalidOperationException("The runtime did not expose trusted platform assemblies.");
            IEnumerable<MetadataReference> references = trustedAssemblies.Split(Path.PathSeparator)
                .Select(static path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("AnalyzerInput", [tree], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.IsEmpty(compilation.GetDiagnostics(testContext.CancellationToken)
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
            var options = new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), onAnalyzerException: null,
                concurrentAnalysis: true, logAnalyzerExecutionTime: true, reportSuppressedDiagnostics: false);
            return await compilation.WithAnalyzers([new CodeQlGenericCatchAnalyzer()], options)
                .GetAnalyzerDiagnosticsAsync(testContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }
}
