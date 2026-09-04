using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies disposable construction is protected before fallible work and ownership transfer.
/// </summary>
/// <param name="testContext">The running test's cooperative cancellation context.</param>
[TestClass]
public sealed class CodeQlLocalDisposableAnalyzerTests(TestContext testContext)
{
    /// <summary>
    /// Verifies a second pipe constructor cannot precede the first pipe's cleanup region.
    /// </summary>
    [TestMethod]
    public async Task ReportsConstructorBeforeCleanupRegion()
    {
        const string Source = """
            using System.IO.Pipes;
            using System.Threading.Tasks;
            internal static class Pipes
            {
                internal static async Task<(NamedPipeServerStream, NamedPipeClientStream)> CreateAsync(string name)
                {
                    var reader = new NamedPipeServerStream(name, PipeDirection.In);
                    var writer = new NamedPipeClientStream(".", name, PipeDirection.Out);
                    try
                    {
                        Task connection = reader.WaitForConnectionAsync();
                        await writer.ConnectAsync();
                        await connection;
                        return (reader, writer);
                    }
                    catch
                    {
                        await writer.DisposeAsync();
                        await reader.DisposeAsync();
                        throw;
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        AssertReportsLocal(diagnostics, "reader");
    }

    /// <summary>
    /// Verifies cleanup also protects earlier variables within a multiple-initializer declaration.
    /// </summary>
    [TestMethod]
    public async Task ReportsSecondInitializerBeforeCleanup()
    {
        const string Source = """
            using System.IO;
            internal static class Streams
            {
                internal static void Open()
                {
                    MemoryStream first = new(), second = new();
                    try { first.WriteByte(1); second.WriteByte(2); }
                    finally { second.Dispose(); first.Dispose(); }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        AssertReportsLocal(diagnostics, "first");
    }

    /// <summary>
    /// Verifies a tuple return does not protect resources from earlier asynchronous failures.
    /// </summary>
    [TestMethod]
    public async Task ReportsTupleFactoryWithoutFailureCleanup()
    {
        const string Source = """
            using System.IO;
            using System.Threading.Tasks;
            internal static class Streams
            {
                internal static async Task<(MemoryStream, int)> CreateAsync()
                {
                    var stream = new MemoryStream();
                    await Task.Yield();
                    return (stream, 1);
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        AssertReportsLocal(diagnostics, "stream");
    }

    /// <summary>
    /// Verifies typed and filtered catches do not protect a tuple factory against every failure.
    /// </summary>
    /// <param name="handler">The exception handler with incomplete coverage.</param>
    [TestMethod]
    [DataRow("catch (IOException)")]
    [DataRow("catch (Exception) when (handle)")]
    public async Task ReportsFactoryWithIncompleteFailureCleanup(string handler)
    {
        string source = $$"""
            using System;
            using System.IO;
            using System.Threading.Tasks;
            internal static class Streams
            {
                internal static async Task<(MemoryStream, int)> CreateAsync(bool handle)
                {
                    var stream = new MemoryStream();
                    try { await Task.Yield(); return (stream, 1); }
                    {{handler}} { stream.Dispose(); throw; }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);

        AssertReportsLocal(diagnostics, "stream");
    }

    /// <summary>
    /// Verifies asynchronous-only disposal is recognized without requiring IDisposable.
    /// </summary>
    [TestMethod]
    public async Task ReportsUnprotectedAsyncDisposableOnlyResource()
    {
        const string Source = """
            using System;
            using System.Threading.Tasks;
            internal sealed class AsyncResource : IAsyncDisposable
            {
                public ValueTask DisposeAsync() => ValueTask.CompletedTask;

                internal static async Task<(AsyncResource, int)> CreateAsync()
                {
                    var resource = new AsyncResource();
                    await Task.Yield();
                    return (resource, 1);
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        AssertReportsLocal(diagnostics, "resource");
    }

    /// <summary>
    /// Verifies non-disposable tuple elements need no cleanup across asynchronous operations.
    /// </summary>
    [TestMethod]
    public async Task AcceptsNonDisposableTupleLocal()
    {
        const string Source = """
            using System.Text;
            using System.Threading.Tasks;
            internal static class TextFactory
            {
                internal static async Task<(StringBuilder, int)> CreateAsync()
                {
                    var text = new StringBuilder();
                    await Task.Yield();
                    return (text, 1);
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies nested ownership protection permits a legitimate asynchronous tuple factory.
    /// </summary>
    [TestMethod]
    public async Task AcceptsExceptionSafeAsyncFactory()
    {
        const string Source = """
            using System;
            using System.IO.Pipes;
            using System.Threading.Tasks;
            internal static class Pipes
            {
                internal static async Task<(NamedPipeServerStream, NamedPipeClientStream)> CreateAsync(string name)
                {
                    var reader = new NamedPipeServerStream(name, PipeDirection.In);
                    try
                    {
                        var writer = new NamedPipeClientStream(".", name, PipeDirection.Out);
                        try
                        {
                            Task connection = reader.WaitForConnectionAsync();
                            await writer.ConnectAsync();
                            await connection;
                            return (reader, writer);
                        }
                        catch (Exception) { await writer.DisposeAsync(); throw; }
                    }
                    catch { await reader.DisposeAsync(); throw; }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies structured using ownership protects each pipe before constructing the next.
    /// </summary>
    [TestMethod]
    public async Task AcceptsScopedConstruction()
    {
        const string Source = """
            using System.IO.Pipes;
            using System.Threading.Tasks;
            internal static class Pipes
            {
                internal static async Task ConnectAsync(string name)
                {
                    await using var reader = new NamedPipeServerStream(name, PipeDirection.In);
                    await using var writer = new NamedPipeClientStream(".", name, PipeDirection.Out);
                    Task connection = reader.WaitForConnectionAsync();
                    await writer.ConnectAsync();
                    await connection;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies immediate direct and tuple returns do not create an exception window after allocation.
    /// </summary>
    [TestMethod]
    public async Task AcceptsImmediateOwnershipTransfer()
    {
        const string Source = """
            using System.IO;
            internal static class Streams
            {
                internal static MemoryStream Create()
                {
                    var stream = new MemoryStream();
                    return stream;
                }
                internal static (MemoryStream, int) CreatePair()
                {
                    var stream = new MemoryStream();
                    return (stream, 1);
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies merely declaring a deferred allocation does not execute it before the cleanup region.
    /// </summary>
    [TestMethod]
    public async Task AcceptsDeferredAllocationBeforeCleanup()
    {
        const string Source = """
            using System;
            using System.IO;
            internal static class Streams
            {
                internal static (MemoryStream, Func<MemoryStream>) Create()
                {
                    var stream = new MemoryStream();
                    Func<MemoryStream> factory = static () => new MemoryStream();
                    try { return (stream, factory); }
                    catch { stream.Dispose(); throw; }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    private static void AssertReportsLocal(ImmutableArray<Diagnostic> diagnostics, string name)
    {
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlLocalDisposableAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains($"'{name}'", diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.StartsWith(name, diagnostic.Location.SourceTree?.GetText()
            .ToString(diagnostic.Location.SourceSpan));
    }

    private async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        string sourcePath = Path.Combine(Path.GetTempPath(), $"csls-disposable-local-{Guid.NewGuid():N}.cs");
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
            return await compilation.WithAnalyzers([new CodeQlLocalDisposableAnalyzer()], analysisOptions)
                .GetAnalyzerDiagnosticsAsync(testContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }
}
