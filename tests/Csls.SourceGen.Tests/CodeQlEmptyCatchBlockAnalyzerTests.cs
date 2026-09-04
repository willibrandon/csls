using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies empty handlers are rejected independently of exception filters and comments.
/// </summary>
/// <param name="testContext">The running test's cooperative cancellation context.</param>
[TestClass]
public sealed class CodeQlEmptyCatchBlockAnalyzerTests(TestContext testContext)
{
    /// <summary>
    /// Verifies catch-all, typed, filtered, comment-only, and empty-statement handlers fail locally.
    /// </summary>
    /// <param name="declaration">The exception declaration and optional filter.</param>
    /// <param name="body">The non-executable handler body.</param>
    [TestMethod]
    [DataRow("", "")]
    [DataRow("(Exception)", "")]
    [DataRow("(InvalidOperationException) when (stopped)", "")]
    [DataRow("(InvalidOperationException) when (stopped)", "// The process exited.\n")]
    [DataRow("(Exception)", "; { }")]
    [DataRow("(Exception)", "static void Unused() { Console.Error.WriteLine(\"Not executed\"); }")]
    public async Task ReportsEmptyExceptionHandler(string declaration, string body)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(declaration, body).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlEmptyCatchBlockAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.IsNotNull(diagnostic.Location.SourceTree);
        SourceText text = await diagnostic.Location.SourceTree.GetTextAsync(testContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual("catch", text.ToString(diagnostic.Location.SourceSpan));
    }

    /// <summary>
    /// Verifies explicit propagation, recovery, and intentional control flow remain accepted.
    /// </summary>
    /// <param name="body">The handler's executable recovery or propagation.</param>
    [TestMethod]
    [DataRow("throw;")]
    [DataRow("Console.Error.WriteLine(\"The process exited.\");")]
    [DataRow("return;")]
    [DataRow("static void Recover() { Console.Error.WriteLine(\"The process exited.\"); } Recover();")]
    public async Task AcceptsExplicitExceptionHandling(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            "(InvalidOperationException) when (stopped)", body).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    private async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string declaration, string body)
    {
        string source = $$"""
            using System;
            internal static class ProcessCleanup
            {
                internal static void Stop(bool stopped)
                {
                    try { Console.WriteLine("Stopping"); }
                    catch {{declaration}}
                    {
                        {{body}}
                    }
                }
            }
            """;
        string sourcePath = Path.Combine(Path.GetTempPath(), $"csls-empty-catch-{Guid.NewGuid():N}.cs");
        try
        {
            await File.WriteAllTextAsync(sourcePath, source, testContext.CancellationToken).ConfigureAwait(false);
            SyntaxTree syntaxTree;
            using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var text = SourceText.From(stream, Encoding.UTF8);
                syntaxTree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.CSharp14),
                    path: sourcePath, cancellationToken: testContext.CancellationToken);
            }

            string trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
                ?? throw new InvalidOperationException("The runtime did not expose trusted platform assemblies.");
            IEnumerable<MetadataReference> references = trustedAssemblies.Split(Path.PathSeparator)
                .Select(static path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("AnalyzerInput", [syntaxTree], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.IsEmpty(compilation.GetDiagnostics(testContext.CancellationToken).Where(static diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error));

            var analysisOptions = new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), onAnalyzerException: null,
                concurrentAnalysis: true, logAnalyzerExecutionTime: true, reportSuppressedDiagnostics: false);
            return await compilation.WithAnalyzers([new CodeQlEmptyCatchBlockAnalyzer()], analysisOptions)
                .GetAnalyzerDiagnosticsAsync(testContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }
}
