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

            var lsp = LspProcessSession.Start(
                "csls-virtual-documents-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
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
        }
        """;
}
