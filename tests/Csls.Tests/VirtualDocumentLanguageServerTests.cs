using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies generated and metadata-backed C# documents through a real worker process.
/// </summary>
[TestClass]
public sealed class VirtualDocumentLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Navigates to real generator output and framework metadata and reads both documents.
    /// </summary>
    [TestMethod]
    public async Task DefinitionsOpenGeneratedAndFrameworkSource()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string workerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        string generatorPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.TestSourceGenerator",
            "debug",
            "Csls.TestSourceGenerator.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        Assert.IsTrue(File.Exists(generatorPath), $"Generator not found at {generatorPath}.");

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-virtual-documents-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            string escapedGeneratorPath = SecurityElement.Escape(generatorPath)
                ?? throw new InvalidOperationException("The generator path could not be escaped.");
            string projectText = $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                  <ItemGroup>
                    <Analyzer Include="{{escapedGeneratorPath}}" />
                  </ItemGroup>
                </Project>
                """;
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                projectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-virtual-documents-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var capabilities = JsonDocument.Parse(
                """
                {
                  "textDocument": {
                    "diagnostic": {}
                  },
                  "experimental": {
                    "csharp": {
                      "metadataUris": true
                    }
                  }
                }
                """);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                capabilities.RootElement,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(
                initialization.GetProperty("capabilities")
                    .GetProperty("definitionProvider")
                    .GetBoolean());
            Assert.IsTrue(
                initialization.GetProperty("capabilities")
                    .GetProperty("experimental")
                    .GetProperty("csharp")
                    .GetProperty("metadataUris")
                    .GetBoolean());
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);

            IReadOnlyList<Location> generatedDefinitions = await lsp.RequestDefinitionsAsync(
                documentPath,
                GetPosition(DocumentText, "GeneratedApi"),
                TestContext.CancellationToken).ConfigureAwait(false);
            Location generatedDefinition = Assert.ContainsSingle(generatedDefinitions);
            Assert.StartsWith(
                "csharp:/generated/",
                generatedDefinition.Uri.ToString(),
                StringComparison.Ordinal);

            CSharpMetadataResponse? generatedDocument = await lsp.RequestCSharpMetadataAsync(
                generatedDefinition.Uri,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(generatedDocument);
            Assert.AreEqual("Fixture", generatedDocument.ProjectName);
            Assert.AreEqual("Fixture", generatedDocument.AssemblyName);
            Assert.AreEqual("GeneratedApi.g.cs", generatedDocument.SymbolName);
            Assert.Contains(
                "public static class GeneratedApi",
                generatedDocument.Source,
                StringComparison.Ordinal);
            Assert.Contains(
                "public const string Message = \"generated\";",
                generatedDocument.Source,
                StringComparison.Ordinal);
            Assert.AreEqual(
                "GeneratedApi",
                GetRangeText(generatedDocument.Source, generatedDefinition.Range));

            IReadOnlyList<Location> frameworkDefinitions = await lsp.RequestDefinitionsAsync(
                documentPath,
                GetPosition(DocumentText, "string.Empty"),
                TestContext.CancellationToken).ConfigureAwait(false);
            Location frameworkDefinition = Assert.ContainsSingle(frameworkDefinitions);
            Assert.StartsWith(
                "csharp:/metadata/",
                frameworkDefinition.Uri.ToString(),
                StringComparison.Ordinal);

            CSharpMetadataResponse? frameworkDocument = await lsp.RequestCSharpMetadataAsync(
                frameworkDefinition.Uri,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(frameworkDocument);
            Assert.AreEqual("Fixture", frameworkDocument.ProjectName);
            Assert.AreEqual("System.Runtime", frameworkDocument.AssemblyName);
            Assert.AreEqual("System.String", frameworkDocument.SymbolName);
            Assert.Contains(
                "sealed class String",
                frameworkDocument.Source,
                StringComparison.Ordinal);
            Assert.Contains(
                "public static readonly String Empty",
                frameworkDocument.Source,
                StringComparison.Ordinal);
            Assert.AreEqual(
                "String",
                GetRangeText(frameworkDocument.Source, frameworkDefinition.Range));

            IReadOnlyList<Location> memberDefinitions = await lsp.RequestDefinitionsAsync(
                documentPath,
                GetPosition(DocumentText, "Length"),
                TestContext.CancellationToken).ConfigureAwait(false);
            Location memberDefinition = Assert.ContainsSingle(memberDefinitions);
            Assert.StartsWith(
                "csharp:/metadata/",
                memberDefinition.Uri.ToString(),
                StringComparison.Ordinal);
            Assert.AreNotEqual(frameworkDefinition.Uri, memberDefinition.Uri);

            CSharpMetadataResponse? memberDocument = await lsp.RequestCSharpMetadataAsync(
                memberDefinition.Uri,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(memberDocument);
            Assert.AreEqual("System.String.Length", memberDocument.SymbolName);
            Assert.AreEqual(
                "Length",
                GetRangeText(memberDocument.Source, memberDefinition.Range));

            IReadOnlyList<Location> extensionMethodDefinitions =
                await lsp.RequestDefinitionsAsync(
                    documentPath,
                    GetPosition(DocumentText, "FirstOrDefault"),
                    TestContext.CancellationToken).ConfigureAwait(false);
            Location extensionMethodDefinition = Assert.ContainsSingle(
                extensionMethodDefinitions);
            Assert.StartsWith(
                "csharp:/metadata/",
                extensionMethodDefinition.Uri.ToString(),
                StringComparison.Ordinal);

            CSharpMetadataResponse? extensionMethodDocument =
                await lsp.RequestCSharpMetadataAsync(
                    extensionMethodDefinition.Uri,
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(extensionMethodDocument);
            Assert.AreEqual("System.Linq", extensionMethodDocument.AssemblyName);
            Assert.Contains(
                "Enumerable.FirstOrDefault",
                extensionMethodDocument.SymbolName,
                StringComparison.Ordinal);
            Assert.AreEqual(
                "FirstOrDefault",
                GetRangeText(
                    extensionMethodDocument.Source,
                    extensionMethodDefinition.Range));

            Assert.IsEmpty(await lsp.RequestDocumentLinksAsync(
                frameworkDefinition.Uri,
                TestContext.CancellationToken).ConfigureAwait(false));
            Assert.IsEmpty(await lsp.RequestFoldingRangesAsync(
                frameworkDefinition.Uri,
                TestContext.CancellationToken).ConfigureAwait(false));
            Assert.IsEmpty(await lsp.RequestDocumentSymbolsAsync(
                frameworkDefinition.Uri,
                TestContext.CancellationToken).ConfigureAwait(false));
            Assert.IsEmpty(await lsp.RequestCodeActionsAsync(
                frameworkDefinition.Uri,
                frameworkDefinition.Range,
                only: null,
                diagnostics: [],
                TestContext.CancellationToken).ConfigureAwait(false));

            CSharpMetadataResponse? malformedDocument = await lsp.RequestCSharpMetadataAsync(
                DocumentUri.Parse("csharp:/metadata/not-a-valid-document.cs"),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNull(malformedDocument);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    /// <summary>
    /// Materializes framework definitions for clients that cannot open virtual documents.
    /// </summary>
    [TestMethod]
    public async Task DefinitionsUseReadableFilesWithoutVirtualDocumentCapability()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-materialized-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """,
                TestContext.CancellationToken).ConfigureAwait(false);
            const string source =
                "bool blank = string.IsNullOrWhiteSpace(null);";
            await File.WriteAllTextAsync(
                documentPath,
                source,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-materialized-metadata-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, source).ConfigureAwait(false);

            IReadOnlyList<Location> definitions = await lsp.RequestDefinitionsAsync(
                documentPath,
                GetPosition(source, "IsNullOrWhiteSpace"),
                TestContext.CancellationToken).ConfigureAwait(false);
            Location definition = Assert.ContainsSingle(definitions);
            Assert.StartsWith(
                "file:",
                definition.Uri.ToString(),
                StringComparison.Ordinal);
            string metadataPath = definition.Uri.GetFileSystemPath();
            Assert.IsTrue(File.Exists(metadataPath), $"Metadata file not found at {metadataPath}.");
            string metadataSource = await File.ReadAllTextAsync(
                metadataPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("class String", metadataSource, StringComparison.Ordinal);
            Assert.Contains("IsNullOrWhiteSpace", metadataSource, StringComparison.Ordinal);
            Assert.AreEqual(
                "IsNullOrWhiteSpace",
                GetRangeText(metadataSource, definition.Range));

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private static Position GetPosition(string source, string value)
    {
        int offset = source.IndexOf(value, StringComparison.Ordinal);
        if (offset < 0)
        {
            throw new InvalidOperationException($"{value} was not found in the test source.");
        }

        int line = 0;
        int character = 0;
        for (int index = 0; index < offset; index++)
        {
            if (source[index] == '\n')
            {
                line++;
                character = 0;
            }
            else
            {
                character++;
            }
        }

        return new Position(line, character);
    }

    private static string GetRangeText(string source, LspRange range)
    {
        string[] lines = source.Split('\n');
        Assert.AreEqual(range.Start.Line, range.End.Line);
        Assert.IsGreaterThanOrEqualTo(0, range.Start.Line);
        Assert.IsLessThan(lines.Length, range.Start.Line);
        return lines[range.Start.Line][range.Start.Character..range.End.Character];
    }

    private const string DocumentText = """
        namespace Fixture;

        public static class Program
        {
            public static string Generated() => GeneratedApi.Message;

            public static int Framework() => string.Empty.Length;

            public static int Linq() => new[] { 1 }.FirstOrDefault();
        }
        """;
}
