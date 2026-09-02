using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies lazy reference CodeLens behavior through real language-server workers.
/// </summary>
[TestClass]
public sealed class CodeLensLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Discovers declarations and resolves exact, capped, and safely refreshed results for VS Code.
    /// </summary>
    [TestMethod]
    public async Task ReferenceCodeLensResolvesThroughRealVsCodeProtocol()
    {
        string source = CreateReferenceSource();
        string fixturePath = CreateFixturePath("vscode");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = await WriteFixtureAsync(fixturePath, source)
                .ConfigureAwait(false);
            var client = new LspTestClient(
                legacyConfiguration: null,
                preferredConfiguration: null);
            LspProcessSession lsp = await StartWorkerAsync(fixturePath, client)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var capabilities = JsonDocument.Parse(
                """
                {
                  "workspace": {
                    "codeLens": {
                      "refreshSupport": true
                    }
                  },
                  "textDocument": {
                    "codeLens": {},
                    "diagnostic": {}
                  }
                }
                """);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                capabilities.RootElement,
                "Visual Studio Code",
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(
                initialization
                    .GetProperty("capabilities")
                    .GetProperty("codeLensProvider")
                    .GetProperty("resolveProvider")
                    .GetBoolean());

            await lsp.OpenDocumentAsync(documentPath, source).ConfigureAwait(false);
            IReadOnlyList<CodeLens> lenses = await lsp.RequestCodeLensesAsync(
                documentPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(19, lenses);
            Assert.IsTrue(lenses.All(static lens => lens.Command is null));
            Assert.IsTrue(lenses.All(static lens => lens.Data is not null));
            Assert.IsTrue(lenses.All(static lens => lens.Range.Start.Line == lens.Range.End.Line));

            string[] identifiers =
            [
                .. lenses.Select(lens => GetRangeText(source, lens.Range))
            ];
            Assert.Contains("Work", identifiers);
            Assert.Contains("IContract", identifiers);
            Assert.Contains("ContractMethod", identifiers);
            Assert.Contains("Mode", identifiers);
            Assert.Contains("First", identifiers);
            Assert.Contains("ValueType", identifiers);
            Assert.Contains("RecordType", identifiers);
            Assert.Contains("Sample", identifiers);
            Assert.Contains("UsedConstant", identifiers);
            Assert.Contains("_usedField", identifiers);
            Assert.Contains("_unusedField", identifiers);
            Assert.Contains("FieldEvent", identifiers);
            Assert.Contains("ExplicitEvent", identifiers);
            Assert.Contains("UsedProperty", identifiers);
            Assert.Contains("Target", identifiers);
            Assert.Contains("Caller", identifiers);
            Assert.Contains("WithLocal", identifiers);
            Assert.DoesNotContain("Local", identifiers);
            Assert.DoesNotContain("this", identifiers);
            Assert.DoesNotContain("operator", identifiers);

            CodeLens oneReference = await lsp.ResolveCodeLensAsync(
                FindLens(lenses, source, "UsedConstant"),
                TestContext.CancellationToken).ConfigureAwait(false);
            CodeLensData originalOneReferenceData = oneReference.Data
                ?? throw new InvalidDataException("The unresolved lens had no resolve data.");
            Assert.IsNotNull(oneReference.Command);
            Assert.AreEqual("1 reference", oneReference.Command.Title);
            Assert.AreEqual("csls.client.peekReferences", oneReference.Command.Command);
            Assert.IsNotNull(oneReference.Command.Arguments);
            Assert.HasCount(2, oneReference.Command.Arguments);
            Assert.AreEqual(
                DocumentUri.FromFileSystemPath(documentPath).ToString(),
                oneReference.Command.Arguments[0].GetString());
            Assert.AreEqual(
                oneReference.Range.Start.Line,
                oneReference.Command.Arguments[1].GetProperty("line").GetInt32());
            Assert.AreEqual(
                oneReference.Range.Start.Character,
                oneReference.Command.Arguments[1].GetProperty("character").GetInt32());

            CodeLens zeroReferences = await lsp.ResolveCodeLensAsync(
                FindLens(lenses, source, "_unusedField"),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(zeroReferences.Command);
            Assert.AreEqual("0 references", zeroReferences.Command.Title);

            CodeLens cappedReferences = await lsp.ResolveCodeLensAsync(
                FindLens(lenses, source, "Target"),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(cappedReferences.Command);
            Assert.AreEqual("99+ references", cappedReferences.Command.Title);

            string shiftedSource = Environment.NewLine + source;
            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = shiftedSource }])
                .ConfigureAwait(false);
            await client.WaitForCodeLensRefreshAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<CodeLens> refreshedLenses = await lsp.RequestCodeLensesAsync(
                documentPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            CodeLens refreshedOneReference = FindLens(
                refreshedLenses,
                shiftedSource,
                "UsedConstant");
            CodeLensData refreshedData = refreshedOneReference.Data
                ?? throw new InvalidDataException("The refreshed lens had no resolve data.");
            Assert.IsGreaterThan(originalOneReferenceData.Generation, refreshedData.Generation);

            CodeLens resolvedAfterRefresh = await lsp.ResolveCodeLensAsync(
                oneReference with { Command = null },
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(resolvedAfterRefresh.Command);
            Assert.AreEqual("1 reference", resolvedAfterRefresh.Command.Title);
            Assert.AreEqual("csls.client.peekReferences", resolvedAfterRefresh.Command.Command);
            Assert.AreEqual(oneReference.Range, resolvedAfterRefresh.Range);
            Assert.IsNotNull(resolvedAfterRefresh.Data);
            Assert.AreEqual(refreshedData.Generation, resolvedAfterRefresh.Data.Generation);

            string sourceWithoutUsedConstant = shiftedSource
                .Replace(
                    "    public const int UsedConstant = 1;",
                    string.Empty,
                    StringComparison.Ordinal)
                .Replace(
                    "        _ = UsedConstant;",
                    string.Empty,
                    StringComparison.Ordinal);
            Assert.DoesNotContain(
                "UsedConstant",
                sourceWithoutUsedConstant,
                StringComparison.Ordinal);
            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 3,
                [new TextDocumentContentChangeEvent { Text = sourceWithoutUsedConstant }])
                .ConfigureAwait(false);
            await client.WaitForCodeLensRefreshAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<CodeLens> lensesWithoutDeclaration = await lsp.RequestCodeLensesAsync(
                documentPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "UsedConstant",
                lensesWithoutDeclaration.Select(lens =>
                    GetRangeText(sourceWithoutUsedConstant, lens.Range)));

            CodeLens unresolvedAfterRemoval = await lsp.ResolveCodeLensAsync(
                oneReference with { Command = null },
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNull(unresolvedAfterRemoval.Command);
            Assert.IsNull(unresolvedAfterRemoval.Data);
            Assert.AreEqual(oneReference.Range, unresolvedAfterRemoval.Range);

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
    /// Resolves the standard show-references command and real locations required by Zed.
    /// </summary>
    [TestMethod]
    public async Task ReferenceCodeLensResolvesThroughRealZedProtocol()
    {
        const string source =
            "namespace Fixture; sealed class ZedTarget { void Called() { } void Caller() { Called(); } }\n";
        string fixturePath = CreateFixturePath("zed");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = await WriteFixtureAsync(fixturePath, source)
                .ConfigureAwait(false);
            LspProcessSession lsp = await StartWorkerAsync(fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var capabilities = JsonDocument.Parse(
                """
                {
                  "textDocument": {
                    "codeLens": {},
                    "diagnostic": {}
                  }
                }
                """);
            await lsp.InitializeAsync(
                fixturePath,
                capabilities.RootElement,
                "Zed",
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, source).ConfigureAwait(false);
            IReadOnlyList<CodeLens> lenses = await lsp.RequestCodeLensesAsync(
                documentPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            CodeLens resolved = await lsp.ResolveCodeLensAsync(
                FindLens(lenses, source, "Called"),
                TestContext.CancellationToken).ConfigureAwait(false);

            Assert.IsNotNull(resolved.Command);
            Assert.AreEqual("1 reference", resolved.Command.Title);
            Assert.AreEqual("editor.action.showReferences", resolved.Command.Command);
            Assert.IsNotNull(resolved.Command.Arguments);
            Assert.HasCount(3, resolved.Command.Arguments);
            JsonElement locations = resolved.Command.Arguments[2];
            Assert.AreEqual(JsonValueKind.Array, locations.ValueKind);
            JsonElement location = Assert.ContainsSingle(locations.EnumerateArray().ToArray());
            Assert.AreEqual(
                DocumentUri.FromFileSystemPath(documentPath).ToString(),
                location.GetProperty("uri").GetString());

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
    /// Includes related constructor definitions when VS Code opens references from a type lens.
    /// </summary>
    [TestMethod]
    public async Task TypeReferenceCodeLensIncludesExplicitConstructorDefinition()
    {
        const string source = """
            namespace Fixture;

            internal sealed class Target
            {
                public Target()
                {
                }
            }

            internal static class Consumer
            {
                internal static object Create() => new Target();
            }
            """;
        string fixturePath = CreateFixturePath("vscode-constructor");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = await WriteFixtureAsync(fixturePath, source)
                .ConfigureAwait(false);
            LspProcessSession lsp = await StartWorkerAsync(fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var capabilities = JsonDocument.Parse(
                """
                {
                  "textDocument": {
                    "codeLens": {},
                    "diagnostic": {}
                  }
                }
                """);
            await lsp.InitializeAsync(
                fixturePath,
                capabilities.RootElement,
                "Visual Studio Code",
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, source).ConfigureAwait(false);

            IReadOnlyList<CodeLens> lenses = await lsp.RequestCodeLensesAsync(
                documentPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            CodeLens resolved = await lsp.ResolveCodeLensAsync(
                lenses.Single(static lens => lens.Range.Start == new Position(2, 22)),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(resolved.Command);
            Assert.AreEqual("2 references", resolved.Command.Title);
            Assert.AreEqual("csls.client.peekReferences", resolved.Command.Command);

            IReadOnlyList<Location> references = await lsp.RequestReferencesAsync(
                documentPath,
                resolved.Range.Start,
                includeDeclaration: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(3, references);
            Assert.AreSequenceEqual(
                [new Position(2, 22), new Position(4, 11), new Position(11, 43)],
                references.Select(static location => location.Range.Start).ToArray());

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private async Task<string> WriteFixtureAsync(string fixturePath, string source)
    {
        string documentPath = Path.Join(fixturePath, "Program.cs");
        await File.WriteAllTextAsync(
            Path.Join(fixturePath, "Fixture.csproj"),
            ProjectText,
            TestContext.CancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            documentPath,
            source,
            TestContext.CancellationToken).ConfigureAwait(false);
        return documentPath;
    }

    private static async Task<LspProcessSession> StartWorkerAsync(
        string fixturePath,
        LspTestClient? client = null)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        return await LspProcessSession.StartAsync(
            "csls-code-lens-worker",
            EditorToolResolver.ResolveDotNetHost(),
            [workerPath],
            fixturePath,
            client).ConfigureAwait(false);
    }

    private static string CreateFixturePath(string client)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        return Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "test-workspaces",
            $"code-lens-{client}-{Guid.NewGuid():N}");
    }

    private static CodeLens FindLens(
        IReadOnlyList<CodeLens> lenses,
        string source,
        string identifier) =>
        lenses.Single(lens => GetRangeText(source, lens.Range) == identifier);

    private static string GetRangeText(string source, LspRange range)
    {
        string line = source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')[range.Start.Line];
        return line[range.Start.Character..range.End.Character];
    }

    private static string CreateReferenceSource()
    {
        var source = new StringBuilder(
            """
            namespace Fixture;

            public delegate void Work();

            public interface IContract
            {
                void ContractMethod();
            }

            public enum Mode
            {
                First
            }

            public struct ValueType
            {
            }

            public record RecordType;

            public sealed class Sample
            {
                public const int UsedConstant = 1;
                private int _usedField;
                private int _unusedField;
                public event Action? FieldEvent;

                public event Action? ExplicitEvent
                {
                    add { }
                    remove { }
                }

                public Sample()
                {
                }

                ~Sample()
                {
                }

                public int UsedProperty => _usedField;

                public int this[int index] => index;

                public static Sample operator +(Sample left, Sample right) => left;

                public void Target()
                {
                }

                public void Caller()
                {
                    _ = UsedConstant;
                    FieldEvent += Target;
                    _ = UsedProperty;
            """);
        for (int index = 0; index < 100; index++)
        {
            source.AppendLine("        Target();");
        }

        source.Append(
            """
                }

                public void WithLocal()
                {
                    void Local()
                    {
                    }

                    Local();
                }
            }
            """);
        return source.ToString();
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;
}
