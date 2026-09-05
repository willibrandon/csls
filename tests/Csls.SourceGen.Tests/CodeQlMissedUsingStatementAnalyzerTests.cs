using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies structured disposable lifetimes are required inside finally cleanup.
/// </summary>
/// <param name="testContext">The running test's cooperative cancellation context.</param>
[TestClass]
public sealed class CodeQlMissedUsingStatementAnalyzerTests(TestContext testContext)
{
    /// <summary>
    /// Verifies registration cleanup is diagnosed even when more cleanup follows it.
    /// </summary>
    [TestMethod]
    public async Task ReportsRegistrationDisposalBeforeRemainingCleanup()
    {
        const string Source = """
            using System.Threading;
            internal static class Reader
            {
                internal static void Read(CancellationToken cancellationToken)
                {
                    CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
                        static state => { }, null);
                    try { cancellationToken.ThrowIfCancellationRequested(); }
                    finally
                    {
                        registration.Dispose();
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        AssertReportsLocal(diagnostics, "registration");
    }

    /// <summary>
    /// Verifies ordinary and interface-cast disposal of a local use the same rule.
    /// </summary>
    /// <param name="cleanup">The manual finally cleanup expression.</param>
    [TestMethod]
    [DataRow("stream.Dispose();")]
    [DataRow("((IDisposable)stream).Dispose();")]
    [DataRow("if (stream != null) { stream.Dispose(); }")]
    public async Task ReportsDisposableLocalInFinally(string cleanup)
    {
        string source = $$"""
            using System;
            using System.IO;
            internal static class Reader
            {
                internal static void Read()
                {
                    var stream = new MemoryStream();
                    try { stream.WriteByte(1); }
                    finally { {{cleanup}} }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);

        AssertReportsLocal(diagnostics, "stream");
    }

    /// <summary>
    /// Verifies a using scope can finish registration cleanup before an outer finally.
    /// </summary>
    [TestMethod]
    public async Task AcceptsUsingRegistrationInsideTry()
    {
        const string Source = """
            using System.Threading;
            internal static class Reader
            {
                internal static void Read(CancellationToken cancellationToken)
                {
                    try
                    {
                        using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
                            static state => { }, null);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    finally { cancellationToken.ThrowIfCancellationRequested(); }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies ordinary disposal outside finally is not a missed using cleanup pattern.
    /// </summary>
    [TestMethod]
    public async Task AcceptsDisposalOutsideFinally()
    {
        const string Source = """
            using System.IO;
            internal static class Reader
            {
                internal static void Read()
                {
                    var stream = new MemoryStream();
                    stream.Dispose();
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies a deferred callback does not inherit the containing finally's cleanup scope.
    /// </summary>
    /// <param name="callback">The deferred function containing explicit disposal.</param>
    [TestMethod]
    [DataRow("return () => stream.Dispose();")]
    [DataRow("void Close() => stream.Dispose(); return Close;")]
    public async Task AcceptsDeferredDisposalDeclaredInsideFinally(string callback)
    {
        string source = $$"""
            using System;
            using System.IO;
            internal static class Reader
            {
                internal static void Read()
                {
                    try { Console.WriteLine("read"); }
                    finally
                    {
                        Func<Action> factory = () => { var stream = new MemoryStream(); {{callback}} };
                        Console.WriteLine(factory);
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies matching names alone do not turn an unrelated method into IDisposable cleanup.
    /// </summary>
    [TestMethod]
    public async Task AcceptsUnrelatedDisposeMethod()
    {
        const string Source = """
            using System;
            internal sealed class Reader : IDisposable
            {
                void IDisposable.Dispose() { }
                internal void Dispose() { }
                internal static void Read()
                {
                    var reader = new Reader();
                    try { Console.WriteLine(reader); }
                    finally { reader.Dispose(); }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    private static void AssertReportsLocal(ImmutableArray<Diagnostic> diagnostics, string localName)
    {
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlMissedUsingStatementAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains($"'{localName}'", diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.IsNotNull(diagnostic.Location.SourceTree);
        Assert.AreEqual(localName, diagnostic.Location.SourceTree.GetText().ToString(diagnostic.Location.SourceSpan));
    }

    private async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        string sourcePath = Path.Join(Path.GetTempPath(), $"csls-missed-using-{Guid.NewGuid():N}.cs");
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
            return await compilation.WithAnalyzers([new CodeQlMissedUsingStatementAnalyzer()], analysisOptions)
                .GetAnalyzerDiagnosticsAsync(testContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }
}
