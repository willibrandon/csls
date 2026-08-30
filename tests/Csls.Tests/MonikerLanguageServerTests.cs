using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies stable C# symbol monikers through a real language-server worker.
/// </summary>
[TestClass]
public sealed class MonikerLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Classifies imported, exported, project-local, and document-local symbols.
    /// </summary>
    [TestMethod]
    public async Task MonikersReturnStableDotNetSymbolIdentities()
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
            $"csls-moniker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-moniker-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(
                initialization
                    .GetProperty("capabilities")
                    .GetProperty("monikerProvider")
                    .GetBoolean());
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);

            Moniker exported = Assert.ContainsSingle(await lsp.RequestMonikersAsync(
                documentPath,
                FindPosition(DocumentText, "Exported<T>"),
                TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual("dotnet", exported.Scheme);
            Assert.AreEqual(MonikerKind.Export, exported.Kind);
            Assert.AreEqual(UniquenessLevel.Group, exported.Unique);
            Assert.EndsWith(
                "::T:Fixture.Exported`1",
                exported.Identifier,
                StringComparison.Ordinal);
            JsonElement monikerJson = await lsp.RequestMonikerJsonAsync(
                documentPath,
                FindPosition(DocumentText, "Exported<T>"),
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement rawMoniker = Assert.ContainsSingle(monikerJson.EnumerateArray().ToArray());
            Assert.AreEqual("export", rawMoniker.GetProperty("kind").GetString());
            Assert.AreEqual("group", rawMoniker.GetProperty("unique").GetString());

            Moniker typeParameter = Assert.ContainsSingle(await lsp.RequestMonikersAsync(
                documentPath,
                FindPosition(DocumentText, "Exported<T>", "T"),
                TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(MonikerKind.Export, typeParameter.Kind);
            Assert.AreEqual(UniquenessLevel.Group, typeParameter.Unique);
            Assert.EndsWith(
                "::T:Fixture.Exported`1#type-parameter/0",
                typeParameter.Identifier,
                StringComparison.Ordinal);

            Moniker exportedReference = Assert.ContainsSingle(await lsp.RequestMonikersAsync(
                documentPath,
                FindPosition(DocumentText, "Exported<int>"),
                TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(exported, exportedReference);

            Moniker imported = Assert.ContainsSingle(await lsp.RequestMonikersAsync(
                documentPath,
                FindPosition(DocumentText, "Text Format"),
                TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(MonikerKind.Import, imported.Kind);
            Assert.AreEqual(UniquenessLevel.Scheme, imported.Unique);
            Assert.Contains(
                "System.Runtime",
                imported.Identifier,
                StringComparison.Ordinal);
            Assert.EndsWith(
                "::T:System.String",
                imported.Identifier,
                StringComparison.Ordinal);

            Moniker aliasTarget = Assert.ContainsSingle(await lsp.RequestMonikersAsync(
                documentPath,
                FindPosition(DocumentText, "System.String", "String"),
                TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(imported, aliasTarget);

            Moniker parameter = Assert.ContainsSingle(await lsp.RequestMonikersAsync(
                documentPath,
                FindPosition(DocumentText, "count)"),
                TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(MonikerKind.Export, parameter.Kind);
            Assert.AreEqual(UniquenessLevel.Group, parameter.Unique);
            Assert.EndsWith(
                "::M:Fixture.Exported`1.Format(System.Int32)#parameter/0",
                parameter.Identifier,
                StringComparison.Ordinal);

            Moniker parameterReference = Assert.ContainsSingle(await lsp.RequestMonikersAsync(
                documentPath,
                FindPosition(DocumentText, "local = count", "count"),
                TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(parameter, parameterReference);

            Moniker local = Assert.ContainsSingle(await lsp.RequestMonikersAsync(
                documentPath,
                FindPosition(DocumentText, "local ="),
                TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(MonikerKind.Local, local.Kind);
            Assert.AreEqual(UniquenessLevel.Document, local.Unique);
            Assert.Contains(
                "::M:Fixture.Exported`1.Format(System.Int32)#Local/local/",
                local.Identifier,
                StringComparison.Ordinal);

            Moniker localReference = Assert.ContainsSingle(await lsp.RequestMonikersAsync(
                documentPath,
                FindPosition(DocumentText, "return local", "local"),
                TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(local, localReference);

            Moniker internalType = Assert.ContainsSingle(await lsp.RequestMonikersAsync(
                documentPath,
                FindPosition(DocumentText, "InternalType"),
                TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(MonikerKind.Local, internalType.Kind);
            Assert.AreEqual(UniquenessLevel.Project, internalType.Unique);
            Assert.EndsWith(
                "::T:Fixture.InternalType",
                internalType.Identifier,
                StringComparison.Ordinal);

            IReadOnlyList<Moniker> namespaceMonikers = await lsp.RequestMonikersAsync(
                documentPath,
                FindPosition(DocumentText, "namespace Fixture", "Fixture"),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsEmpty(namespaceMonikers);

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
    /// Treats an unsigned source symbol from a referenced project as a group-unique import.
    /// </summary>
    [TestMethod]
    public async Task ReferencedProjectSymbolsReturnImportMonikers()
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
            $"csls-project-moniker-{Guid.NewGuid():N}");
        string libraryPath = Path.Join(fixturePath, "Library");
        string appPath = Path.Join(fixturePath, "App");
        Directory.CreateDirectory(libraryPath);
        Directory.CreateDirectory(appPath);
        try
        {
            string appDocumentPath = Path.Join(appPath, "Program.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.slnx"),
                SolutionText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(libraryPath, "Library.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(libraryPath, "Shared.cs"),
                LibraryDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(appPath, "App.csproj"),
                AppProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                appDocumentPath,
                AppDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-project-moniker-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(appDocumentPath, AppDocumentText).ConfigureAwait(false);

            Moniker imported = Assert.ContainsSingle(await lsp.RequestMonikersAsync(
                appDocumentPath,
                FindPosition(AppDocumentText, "Shared Value"),
                TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual("dotnet", imported.Scheme);
            Assert.AreEqual(MonikerKind.Import, imported.Kind);
            Assert.AreEqual(UniquenessLevel.Group, imported.Unique);
            Assert.EndsWith(
                "::T:Library.Shared",
                imported.Identifier,
                StringComparison.Ordinal);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private static Position FindPosition(
        string text,
        string context,
        string? value = null)
    {
        int contextOffset = text.IndexOf(context, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, contextOffset, $"Context not found: {context}");
        int offset = contextOffset + (value is null
            ? 0
            : context.IndexOf(value, StringComparison.Ordinal));
        string prefix = text[..offset];
        int line = prefix.Count(static character => character == '\n');
        int lastLineBreak = prefix.LastIndexOf('\n');
        return new Position(line, offset - lastLineBreak - 1);
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private const string AppProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="../Library/Library.csproj" />
          </ItemGroup>
        </Project>
        """;

    private const string SolutionText = """
        <Solution>
          <Project Path="App/App.csproj" />
          <Project Path="Library/Library.csproj" />
        </Solution>
        """;

    private const string LibraryDocumentText = """
        namespace Library;

        public sealed class Shared
        {
        }
        """;

    private const string AppDocumentText = """
        using Library;

        namespace App;

        public sealed class Program
        {
            public Shared Value { get; } = new();
        }
        """;

    private const string DocumentText = """
        using Text = System.String;

        namespace Fixture;

        public sealed class Exported<T>
        {
            public Text Format(int count)
            {
                int local = count;
                return local.ToString();
            }
        }

        internal sealed class InternalType
        {
        }

        public static class Program
        {
            public static void Main()
            {
                Exported<int> value = new();
                _ = value.Format(1);
            }
        }
        """;
}
