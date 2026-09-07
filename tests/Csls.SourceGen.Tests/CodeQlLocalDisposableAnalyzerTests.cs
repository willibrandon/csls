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
    /// Rejects factory-created resources transferred after an earlier owner's fallible cleanup.
    /// </summary>
    /// <param name="factory">The resource acquisition expression.</param>
    [TestMethod]
    [DataRow("File.OpenRead(path)")]
    [DataRow("choose ? File.OpenRead(path) : File.OpenRead(alternate)")]
    [DataRow("(File.OpenRead(path))")]
    public async Task ReportsFactoryResultTransferAfterFallibleCleanup(string factory)
    {
        string source = $$"""
            using System.IO;
            internal static class Reader
            {
                internal static void Read(string path, string alternate, bool choose)
                {
                    FileStream current = null;
                    try
                    {
                        current = File.OpenRead(path);
                        for (int index = 0; index < 2; index++)
                        {
                            FileStream child = {{factory}};
                            current.Dispose();
                            current = child;
                        }
                    }
                    finally { current?.Dispose(); }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);
        AssertReportsLocal(diagnostics, "child");
    }

    /// <summary>
    /// Preserves transfers with no intervening failure and transfers protected by unconditional cleanup.
    /// </summary>
    /// <param name="transfer">The protected or immediate local ownership transfer.</param>
    [TestMethod]
    [DataRow("current = child;")]
    [DataRow("try { current.Dispose(); current = child; } catch { child.Dispose(); throw; }")]
    [DataRow("try { current.Dispose(); current = child; } catch { using (child) { } throw; }")]
    public async Task AcceptsProtectedFactoryResultTransfer(string transfer)
    {
        string source = $$"""
            using System.IO;
            internal static class Reader
            {
                internal static void Read(string path)
                {
                    FileStream current = null;
                    try
                    {
                        FileStream child = File.OpenRead(path);
                        {{transfer}}
                    }
                    finally { current?.Dispose(); }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);
        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Accepts factory-created arrays whose type has no containing assembly or disposable contract.
    /// </summary>
    [TestMethod]
    public async Task AcceptsArrayFactoryWithoutContainingAssembly()
    {
        const string Source = """
            using System;
            internal static class Reader
            {
                internal static int Read()
                {
                    int[] values = Array.Empty<int>();
                    return values.Length;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);
        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Rejects a factory-owned stream whose cleanup starts after another throwing constructor.
    /// </summary>
    [TestMethod]
    public async Task ReportsFactoryBeforeCleanupRegion()
    {
        const string Source = """
            using System.IO;
            using System.Diagnostics;
            internal static class Reader
            {
                internal static FileStream Read(string path)
                {
                    FileStream stream = File.OpenRead(path);
                    var start = new ProcessStartInfo();
                    try { start.FileName = path; return stream; }
                    catch { stream.Dispose(); throw; }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);
        AssertReportsLocal(diagnostics, "stream");
    }

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
    /// Verifies conditional validation cleanup does not protect a handle factory's initialization.
    /// </summary>
    [TestMethod]
    public async Task ReportsSafeHandleInitializationBeforeCleanup()
    {
        const string Source = """
            using System;
            using Microsoft.Win32.SafeHandles;
            internal sealed class NativeHandle : SafeHandleZeroOrMinusOneIsInvalid
            {
                private NativeHandle() : base(true) { }
                protected override bool ReleaseHandle() => true;

                internal static NativeHandle Open(uint port, int result)
                {
                    var handle = new NativeHandle();
                    handle.SetHandle(checked((nint)port));
                    if (result != 0 || handle.IsInvalid)
                    {
                        handle.Dispose();
                        throw new InvalidOperationException();
                    }

                    return handle;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        AssertReportsLocal(diagnostics, "handle");
    }

    /// <summary>
    /// Verifies direct handle returns require exception cleanup after fallible initialization.
    /// </summary>
    /// <param name="handler">The absent or incomplete initialization cleanup.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("catch (IOException) { handle.Dispose(); throw; }")]
    [DataRow("catch (Exception) when (cleanup) { handle.Dispose(); throw; }")]
    public async Task ReportsHandleFactoryWithUnprotectedInitialization(string handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        string initialization = handler.Length == 0
            ? "handle.SetHandle(port); return handle;"
            : $"try {{ handle.SetHandle(port); return handle; }} {handler}";
        string source = $$"""
            using System;
            using System.IO;
            using Microsoft.Win32.SafeHandles;
            internal sealed class NativeHandle : SafeHandleZeroOrMinusOneIsInvalid
            {
                private NativeHandle() : base(true) { }
                protected override bool ReleaseHandle() => true;

                internal static NativeHandle Open(nint port, bool cleanup)
                {
                    var handle = new NativeHandle();
                    {{initialization}}
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);

        AssertReportsLocal(diagnostics, "handle");
    }

    /// <summary>
    /// Verifies handle initialization and validation share unconditional exception cleanup.
    /// </summary>
    [TestMethod]
    public async Task AcceptsProtectedSafeHandleFactory()
    {
        const string Source = """
            using System;
            using Microsoft.Win32.SafeHandles;
            internal sealed class NativeHandle : SafeHandleZeroOrMinusOneIsInvalid
            {
                private NativeHandle() : base(true) { }
                protected override bool ReleaseHandle() => true;

                internal static NativeHandle Open(uint port, int result)
                {
                    var handle = new NativeHandle();
                    try
                    {
                        handle.SetHandle(checked((nint)port));
                        if (result != 0 || handle.IsInvalid)
                        {
                            throw new InvalidOperationException();
                        }

                        return handle;
                    }
                    catch
                    {
                        handle.Dispose();
                        throw;
                    }
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

    /// <summary>
    /// Verifies configured disposal aliases do not conceal a library resource's ownership.
    /// </summary>
    /// <param name="declaration">The constructed library resource declaration.</param>
    /// <param name="cleanupType">The explicit or inferred configured cleanup type.</param>
    [TestMethod]
    [DataRow("var stream = new FileStream(path, FileMode.CreateNew);", "ConfiguredAsyncDisposable")]
    [DataRow("FileStream stream = new(path, FileMode.CreateNew);", "var")]
    [DataRow("var stream = new MemoryStream();", "var")]
    public async Task ReportsLibraryResourceWithConfiguredDisposalAlias(string declaration, string cleanupType)
    {
        string source = $$"""
            using System.IO;
            using System.Runtime.CompilerServices;
            using System.Threading.Tasks;
            internal static class Streams
            {
                internal static async Task WriteAsync(string path)
                {
                    {{declaration}}
                    await using {{cleanupType}} cleanup = stream.ConfigureAwait(false);
                    await stream.WriteAsync(new byte[] { 1 });
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);

        AssertReportsLocal(diagnostics, "stream");
    }

    /// <summary>
    /// Verifies configured using blocks expose each library resource to the same ownership check as aliases.
    /// </summary>
    /// <param name="constructor">The stream constructor observed by remote analysis.</param>
    [TestMethod]
    [DataRow("new FileStream(path, FileMode.CreateNew)")]
    [DataRow("new StreamWriter(path)")]
    public async Task ReportsLibraryResourceWithConfiguredDisposalBlock(string constructor)
    {
        string source = $$"""
            using System.IO;
            using System.Threading.Tasks;
            internal static class Streams
            {
                internal static async Task WriteAsync(string path)
                {
                    var stream = {{constructor}};
                    await using (stream.ConfigureAwait(false))
                    {
                        await Task.Yield();
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);

        AssertReportsLocal(diagnostics, "stream");
    }

    /// <summary>
    /// Verifies a directly scoped library resource has visible exception-safe cleanup.
    /// </summary>
    [TestMethod]
    public async Task AcceptsDirectlyScopedLibraryResource()
    {
        const string Source = """
            using System.IO;
            using System.Threading.Tasks;
            internal static class Streams
            {
                internal static async Task WriteAsync(string path)
                {
                    using var stream = new FileStream(path, FileMode.CreateNew);
                    await stream.WriteAsync(new byte[] { 1 }).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies nested direct scopes protect both the backup stream and its asynchronously flushed writer.
    /// </summary>
    [TestMethod]
    public async Task AcceptsDirectlyScopedStreamAndWriter()
    {
        const string Source = """
            using System.IO;
            using System.Threading.Tasks;
            internal static class Streams
            {
                internal static async Task WriteAsync(string path)
                {
                    using (var stream = new FileStream(path, FileMode.CreateNew))
                    using (var writer = new StreamWriter(stream))
                    {
                        await writer.WriteAsync("policy").ConfigureAwait(false);
                        await writer.FlushAsync().ConfigureAwait(false);
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies application-owned asynchronous disposal can remain in a configured scope.
    /// </summary>
    [TestMethod]
    public async Task AcceptsConfiguredApplicationResource()
    {
        const string Source = """
            using System;
            using System.Threading.Tasks;
            internal sealed class AsyncResource : IAsyncDisposable
            {
                public ValueTask DisposeAsync() => ValueTask.CompletedTask;

                internal static async Task UseAsync()
                {
                    var resource = new AsyncResource();
                    await using var cleanup = resource.ConfigureAwait(false);
                    await Task.Yield();
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
        string sourcePath = Path.Join(Path.GetTempPath(), $"csls-disposable-local-{Guid.NewGuid():N}.cs");
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
