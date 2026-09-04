using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Globalization;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies repository diagnostics by running the analyzer through a real Roslyn compilation.
/// </summary>
[TestClass]
public sealed class RepositoryConventionAnalyzerTests
{
    /// <summary>
    /// Verifies every additional type declaration is rejected.
    /// </summary>
    [TestMethod]
    public async Task ReportsEveryAdditionalTypeInAFile()
    {
        const string Source = """
            /// <summary>
            /// Represents the first type.
            /// </summary>
            internal sealed class FirstType;

            /// <summary>
            /// Represents the second type.
            /// </summary>
            internal sealed class SecondType;
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(RepositoryConventionAnalyzer.OneTypePerFileDiagnosticId, diagnostic.Id);
        Assert.Contains("SecondType", diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Verifies undocumented internal members are rejected.
    /// </summary>
    [TestMethod]
    public async Task ReportsUndocumentedInternalMember()
    {
        const string Source = """
            /// <summary>
            /// Represents a documented type.
            /// </summary>
            internal sealed class DocumentedType
            {
                internal void UndocumentedMethod()
                {
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(RepositoryConventionAnalyzer.XmlDocumentationDiagnosticId, diagnostic.Id);
        Assert.Contains(
            "UndocumentedMethod",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Verifies single-line summaries are rejected.
    /// </summary>
    [TestMethod]
    public async Task ReportsSingleLineSummary()
    {
        const string Source = """
            /// <summary>Represents an invalid summary.</summary>
            internal sealed class DocumentedType;
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(RepositoryConventionAnalyzer.ThreeLineSummaryDiagnosticId, diagnostic.Id);
    }

    /// <summary>
    /// Reports a large malformed summary without failing the analyzer process.
    /// </summary>
    [TestMethod]
    public async Task ReportsLargeMalformedSummaryWithoutAnalyzerFailure()
    {
        string source = $$"""
            /// <summary>
            /// {{new string('x', 1_000_000)}}
            /// Represents an extra summary line.
            /// </summary>
            internal sealed class DocumentedType;
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(RepositoryConventionAnalyzer.ThreeLineSummaryDiagnosticId, diagnostic.Id);
    }

    /// <summary>
    /// Verifies incorrectly named private static fields fail the real analyzer compilation.
    /// </summary>
    [TestMethod]
    public async Task ReportsStaticFieldWithoutPrefix()
    {
        const string Source = """
            /// <summary>
            /// Represents a documented type.
            /// </summary>
            internal sealed class DocumentedType
            {
                private static readonly string[] RequiredTargets = ["Compile"];
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(RepositoryConventionAnalyzer.StaticFieldPrefixDiagnosticId, diagnostic.Id);
        Assert.Contains("RequiredTargets", diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Verifies correctly prefixed static fields and Pascal-cased constants remain valid.
    /// </summary>
    [TestMethod]
    public async Task AcceptsStaticFieldPrefixAndConstantName()
    {
        const string Source = """
            /// <summary>
            /// Represents a documented type.
            /// </summary>
            internal sealed class DocumentedType
            {
                private const string OptionalTarget = "Markup";
                private static readonly string[] s_requiredTargets = ["Compile"];
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies a documented single-type file satisfies every repository diagnostic.
    /// </summary>
    [TestMethod]
    public async Task AcceptsDocumentedOneTypeFile()
    {
        const string Source = """
            /// <summary>
            /// Represents a documented type.
            /// </summary>
            internal sealed class DocumentedType
            {
                /// <summary>
                /// Performs documented work.
                /// </summary>
                internal void DocumentedMethod()
                {
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies compiler-generated top-level entry-point symbols require no documentation.
    /// </summary>
    [TestMethod]
    public async Task AcceptsTopLevelStatements()
    {
        const string Source = "Console.WriteLine(\"csls\");";

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            OutputKind.ConsoleApplication).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies Portable PDB document mappings are projected before iteration.
    /// </summary>
    [TestMethod]
    public async Task ReportsPortablePdbDocumentMappingInsideForEach()
    {
        const string Source = """
            namespace Csls.Debugger;

            internal sealed class DocumentHandle;
            internal sealed class ManagedSymbolDocument;
            internal static class PortablePdbSourceDocumentReader
            {
                internal static ManagedSymbolDocument Read(DocumentHandle handle) => new();
            }

            internal static class Reader
            {
                internal static void ReadAll(System.Collections.Generic.IEnumerable<DocumentHandle> handles)
                {
                    foreach (DocumentHandle handle in handles)
                    {
                        ManagedSymbolDocument document = PortablePdbSourceDocumentReader.Read(handle);
                        _ = document;
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            new CodeQlMissedSelectAnalyzer()).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlMissedSelectAnalyzer.DiagnosticId, diagnostic.Id);
    }

    /// <summary>
    /// Verifies metadata-provider construction is projected before iteration.
    /// </summary>
    [TestMethod]
    public async Task ReportsMetadataProviderMappingInsideForEach()
    {
        const string Source = """
            using System.Collections.Generic;
            using System.Collections.Immutable;
            using System.Reflection.Metadata;

            internal static class Reader
            {
                internal static void ReadAll(IEnumerable<byte[]> images)
                {
                    foreach (byte[] image in images)
                    {
                        MetadataReaderProvider provider = MetadataReaderProvider.FromMetadataImage(
                            ImmutableArray.Create(image));
                        _ = provider.GetMetadataReader();
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            new CodeQlMissedSelectAnalyzer()).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlMissedSelectAnalyzer.DiagnosticId, diagnostic.Id);
    }

    /// <summary>
    /// Verifies custom-attribute metadata is projected before iteration.
    /// </summary>
    [TestMethod]
    public async Task ReportsCustomAttributeMappingInsideForEach()
    {
        const string Source = """
            using System.Reflection.Metadata;

            internal static class Reader
            {
                internal static void ReadAll(
                    MetadataReader metadata,
                    CustomAttributeHandleCollection handles)
                {
                    foreach (CustomAttributeHandle handle in handles)
                    {
                        CustomAttribute attribute = metadata.GetCustomAttribute(handle);
                        _ = attribute;
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            new CodeQlMissedSelectAnalyzer()).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlMissedSelectAnalyzer.DiagnosticId, diagnostic.Id);
    }

    /// <summary>
    /// Verifies initialization-only non-public fields require readonly semantics.
    /// </summary>
    [TestMethod]
    public async Task ReportsInitializationOnlyFieldWithoutReadonlyModifier()
    {
        const string Source = """
            internal sealed class Fixture
            {
                internal int Value = 42;
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            new CodeQlMissedReadonlyModifierAnalyzer()).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlMissedReadonlyModifierAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Contains("Value", diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Verifies fields mutated after initialization remain writable.
    /// </summary>
    [TestMethod]
    public async Task AcceptsFieldMutatedOutsideConstructor()
    {
        const string Source = """
            internal sealed class Fixture
            {
                internal int Value;

                internal void Update() => Value = 42;
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            new CodeQlMissedReadonlyModifierAnalyzer()).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies lazy coalescing assignment preserves intentional field mutability.
    /// </summary>
    [TestMethod]
    public async Task AcceptsFieldMutatedByCoalescingAssignment()
    {
        const string Source = """
            internal sealed class Fixture
            {
                private object? _value;

                internal object GetValue() => _value ??= new object();
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            new CodeQlMissedReadonlyModifierAnalyzer()).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies methods with more than two by-reference parameters are rejected.
    /// </summary>
    [TestMethod]
    public async Task ReportsMethodWithThreeByReferenceParameters()
    {
        const string Source = """
            internal static class Reader
            {
                internal static void Read(ref int first, ref int second, out int third)
                {
                    third = first + second;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            new CodeQlTooManyRefParametersAnalyzer()).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlTooManyRefParametersAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Contains("3", diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Verifies two by-reference parameters remain within the local CodeQL limit.
    /// </summary>
    [TestMethod]
    public async Task AcceptsMethodWithTwoByReferenceParameters()
    {
        const string Source = """
            internal static class Reader
            {
                internal static void Read(ref int first, out int second)
                {
                    second = first;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            new CodeQlTooManyRefParametersAnalyzer()).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies externally imposed interface signatures are not rewritten locally.
    /// </summary>
    [TestMethod]
    public async Task AcceptsInterfaceImplementationWithThreeByReferenceParameters()
    {
        const string Source = """
            internal interface IReader
            {
                void Read(out int first, out int second, out int third);
            }

            internal sealed class Reader : IReader
            {
                public void Read(out int first, out int second, out int third)
                {
                    first = 1;
                    second = 2;
                    third = 3;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            new CodeQlTooManyRefParametersAnalyzer()).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies metadata providers are not manually disposed by a finally loop.
    /// </summary>
    [TestMethod]
    public async Task ReportsMetadataProviderDisposeInsideFinallyForEach()
    {
        const string Source = """
            using System.Collections.Generic;
            using System.Reflection.Metadata;

            internal static class Reader
            {
                internal static void DisposeAll(IReadOnlyList<MetadataReaderProvider> providers)
                {
                    try
                    {
                    }
                    finally
                    {
                        foreach (MetadataReaderProvider provider in providers)
                        {
                            provider.Dispose();
                        }
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            new CodeQlMissedUsingStatementAnalyzer()).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlMissedUsingStatementAnalyzer.DiagnosticId, diagnostic.Id);
    }

    /// <summary>
    /// Verifies ordinary metadata-provider iteration does not require disposal syntax.
    /// </summary>
    [TestMethod]
    public async Task AcceptsMetadataProviderIterationOutsideFinally()
    {
        const string Source = """
            using System.Collections.Generic;
            using System.Reflection.Metadata;

            internal static class Reader
            {
                internal static void ReadAll(IReadOnlyList<MetadataReaderProvider> providers)
                {
                    foreach (MetadataReaderProvider provider in providers)
                    {
                        _ = provider.GetMetadataReader();
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            new CodeQlMissedUsingStatementAnalyzer()).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies a terminal validation branch inside a sequence loop is rejected for CodeQL parity.
    /// </summary>
    [TestMethod]
    public async Task ReportsImplicitSequenceFilterInsideForEach()
    {
        const string Source = """
            using System;
            using System.Collections.Generic;

            internal static class TokenValidator
            {
                internal static void Validate(IReadOnlyList<int> tokens)
                {
                    foreach (int token in tokens)
                    {
                        if (token < 0)
                        {
                            throw new ArgumentOutOfRangeException(nameof(tokens));
                        }
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            new CodeQlMissedWhereAnalyzer()).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlMissedWhereAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Contains("token", diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Verifies explicit sequence filtering satisfies the local CodeQL parity rule.
    /// </summary>
    [TestMethod]
    public async Task AcceptsExplicitWhereBeforeForEach()
    {
        const string Source = """
            using System.Collections.Generic;
            using System.Linq;

            internal static class TokenConsumer
            {
                internal static void Consume(IReadOnlyList<int> tokens)
                {
                    foreach (int token in tokens.Where(static value => value >= 0))
                    {
                        _ = token;
                    }
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            new CodeQlMissedWhereAnalyzer()).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies disposable collections require an exception-safe using lifetime.
    /// </summary>
    [TestMethod]
    public async Task ReportsDisposableCollectionWithoutUsingDeclaration()
    {
        const string Source = """
            using System;
            using System.IO;

            namespace Csls.Debugger;

            internal sealed class DisposableCollection<T> : IDisposable
                where T : class, IDisposable
            {
                public void Dispose()
                {
                }
            }

            internal static class Reader
            {
                internal static void ReadAll()
                {
                    var collection = new DisposableCollection<MemoryStream>();
                    collection.Dispose();
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            new CodeQlDisposeOnThrowAnalyzer()).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlDisposeOnThrowAnalyzer.DiagnosticId, diagnostic.Id);
    }

    /// <summary>
    /// Verifies a using declaration supplies the disposable collection lifetime.
    /// </summary>
    [TestMethod]
    public async Task AcceptsDisposableCollectionUsingDeclaration()
    {
        const string Source = """
            using System;
            using System.IO;

            namespace Csls.Debugger;

            internal sealed class DisposableCollection<T> : IDisposable
                where T : class, IDisposable
            {
                public void Dispose()
                {
                }
            }

            internal static class Reader
            {
                internal static void ReadAll()
                {
                    using var collection = new DisposableCollection<MemoryStream>();
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            new CodeQlDisposeOnThrowAnalyzer()).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies transferred local disposables retain an explicit using scope.
    /// </summary>
    [TestMethod]
    public async Task ReportsUnscopedDisposableTransferredToCollection()
    {
        const string Source = """
            using System;
            using System.Collections.Generic;
            using System.IO;

            namespace Csls.Debugger;

            internal sealed class DisposableCollection<T> : IDisposable
                where T : class, IDisposable
            {
                internal T Acquire(Func<T> factory) => factory();

                public void Dispose()
                {
                }
            }

            internal static class Reader
            {
                internal static void ReadAll()
                {
                    using var collection = new DisposableCollection<MemoryStream>();
                    var stream = new MemoryStream();
                    _ = collection.Acquire(() => stream);
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            new CodeQlLocalDisposableAnalyzer()).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlLocalDisposableAnalyzer.DiagnosticId, diagnostic.Id);
    }

    /// <summary>
    /// Verifies scoped local disposables may transfer into collection ownership.
    /// </summary>
    [TestMethod]
    public async Task AcceptsScopedDisposableTransferredToCollection()
    {
        const string Source = """
            using System;
            using System.IO;

            namespace Csls.Debugger;

            internal sealed class DisposableCollection<T> : IDisposable
                where T : class, IDisposable
            {
                internal T Acquire(Func<T> factory) => factory();

                public void Dispose()
                {
                }
            }

            internal static class Reader
            {
                internal static void ReadAll()
                {
                    using var collection = new DisposableCollection<MemoryStream>();
                    using var stream = new MemoryStream();
                    _ = collection.Acquire(() => stream);
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            new CodeQlLocalDisposableAnalyzer()).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Verifies conditional throw expressions cannot form a simplifiable Boolean expression.
    /// </summary>
    [TestMethod]
    public async Task ReportsBooleanConditionalWithThrowExpression()
    {
        const string Source = """
            using System;

            internal static class Display
            {
                internal static bool Open(bool fail) => fail
                    ? throw new InvalidOperationException()
                    : false;
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            new CodeQlSimplifiableBooleanExpressionAnalyzer()).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlSimplifiableBooleanExpressionAnalyzer.DiagnosticId, diagnostic.Id);
    }

    /// <summary>
    /// Verifies explicit statement control flow satisfies the local CodeQL parity rule.
    /// </summary>
    [TestMethod]
    public async Task AcceptsStatementControlFlowForBooleanFailure()
    {
        const string Source = """
            using System;

            internal static class Display
            {
                internal static bool Open(bool fail)
                {
                    if (fail)
                    {
                        throw new InvalidOperationException();
                    }

                    return false;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(
            Source,
            new CodeQlSimplifiableBooleanExpressionAnalyzer()).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string source,
        OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary)
    {
        return await AnalyzeAsync(
            source,
            new RepositoryConventionAnalyzer(),
            outputKind).ConfigureAwait(false);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string source,
        DiagnosticAnalyzer analyzer,
        OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary)
    {
        var parseOptions = new CSharpParseOptions(
            LanguageVersion.CSharp14,
            DocumentationMode.Diagnose);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions, path: "Input.cs");
        string trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException("The runtime did not expose trusted platform assemblies.");
        IEnumerable<MetadataReference> references = trustedAssemblies
            .Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "AnalyzerInput",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(outputKind));
        ImmutableArray<DiagnosticAnalyzer> analyzers = [analyzer];

        return await compilation
            .WithAnalyzers(analyzers)
            .GetAnalyzerDiagnosticsAsync()
            .ConfigureAwait(false);
    }
}
