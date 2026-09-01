using Csls.Protocol;
using StreamJsonRpc;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies document symbols, workspace symbols, and signature help through a real worker.
/// </summary>
[TestClass]
public sealed class SymbolLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Returns hierarchical declarations, resolvable search results, and overload-aware signatures.
    /// </summary>
    [TestMethod]
    public async Task SymbolsAndSignatureHelpReturnSemanticResults()
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
            $"csls-symbols-{Guid.NewGuid():N}");
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
                "csls-symbol-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement capabilities = initialization.GetProperty("capabilities");
            Assert.IsTrue(capabilities.GetProperty("documentSymbolProvider").GetBoolean());
            Assert.IsTrue(
                capabilities
                    .GetProperty("workspaceSymbolProvider")
                    .GetProperty("resolveProvider")
                    .GetBoolean());
            JsonElement signatureProvider = capabilities.GetProperty("signatureHelpProvider");
            Assert.Contains(
                "(",
                signatureProvider
                    .GetProperty("triggerCharacters")
                    .EnumerateArray()
                    .Select(static character => character.GetString()));
            Assert.Contains(
                ",",
                signatureProvider
                    .GetProperty("triggerCharacters")
                    .EnumerateArray()
                    .Select(static character => character.GetString()));
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);

            IReadOnlyList<DocumentSymbol> documentSymbols =
                await lsp.RequestDocumentSymbolsAsync(
                    documentPath,
                    TestContext.CancellationToken).ConfigureAwait(false);
            DocumentSymbol sourceNamespace = Assert.ContainsSingle(documentSymbols);
            Assert.AreEqual("Fixture", sourceNamespace.Name);
            Assert.AreEqual(SymbolKind.Namespace, sourceNamespace.Kind);
            Assert.IsNotNull(sourceNamespace.Children);
            DocumentSymbol calculator = sourceNamespace.Children.Single(
                static symbol => symbol.Name == "Calculator");
            Assert.AreEqual(SymbolKind.Class, calculator.Kind);
            Assert.AreEqual(new Position(2, 20), calculator.SelectionRange.Start);
            Assert.AreEqual(new Position(2, 30), calculator.SelectionRange.End);
            Assert.IsNotNull(calculator.Children);
            Assert.Contains(
                "Name",
                calculator.Children.Select(static symbol => symbol.Name));
            Assert.AreEqual(
                2,
                calculator.Children.Count(static symbol => symbol.Name == "Combine"));

            IReadOnlyList<WorkspaceSymbol> workspaceSymbols =
                await lsp.RequestWorkspaceSymbolsAsync(
                    "Comb",
                    TestContext.CancellationToken).ConfigureAwait(false);
            WorkspaceSymbol[] combineSymbols =
            [
                .. workspaceSymbols.Where(static symbol => symbol.Name == "Combine")
            ];
            Assert.HasCount(2, combineSymbols);
            Assert.IsTrue(combineSymbols.All(static symbol => symbol.Location.Range is null));
            WorkspaceSymbol resolved = await lsp.ResolveWorkspaceSymbolAsync(
                combineSymbols[0],
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(DocumentUri.FromFileSystemPath(documentPath), resolved.Location.Uri);
            LspRange resolvedRange = resolved.Location.Range ?? throw new InvalidDataException(
                "The resolved workspace symbol had no source range.");
            Assert.Contains(
                resolvedRange.Start,
                new[] { new Position(6, 22), new Position(11, 25) });

            SignatureHelp? signatureHelp = await lsp.RequestSignatureHelpAsync(
                documentPath,
                new Position(21, 43),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(signatureHelp);
            Assert.HasCount(2, signatureHelp.Signatures);
            Assert.AreEqual(1, signatureHelp.ActiveParameter);
            int activeSignatureIndex = signatureHelp.ActiveSignature ??
                throw new InvalidDataException("Signature help had no active signature.");
            SignatureInformation activeSignature =
                signatureHelp.Signatures[activeSignatureIndex];
            Assert.Contains("int", activeSignature.Label, StringComparison.Ordinal);
            Assert.IsNotNull(activeSignature.Parameters);
            Assert.HasCount(2, activeSignature.Parameters);
            Assert.AreEqual("int left", activeSignature.Parameters[0].Label);
            Assert.AreEqual("int right", activeSignature.Parameters[1].Label);
            SignatureInformation stringSignature = signatureHelp.Signatures.Single(
                static signature => signature.Label.Contains("string", StringComparison.Ordinal));
            Assert.Contains("string", stringSignature.Label, StringComparison.Ordinal);

            SignatureHelp? inactiveSignatureHelp = await lsp.RequestSignatureHelpAsync(
                documentPath,
                new Position(2, 22),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNull(inactiveSignatureHelp);

            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = DocumentText + Environment.NewLine }])
                .ConfigureAwait(false);
            RemoteInvocationException staleResolve =
                await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
                    async () => await lsp.ResolveWorkspaceSymbolAsync(
                        combineSymbols[0],
                        TestContext.CancellationToken).ConfigureAwait(false))
                    .ConfigureAwait(false);
            Assert.Contains(
                "retired workspace generation",
                staleResolve.Message,
                StringComparison.Ordinal);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private const string DocumentText = """
        namespace Fixture;

        public static class Calculator
        {
            public static string Name { get; } = "Calculator";

            public static int Combine(int left, int right)
            {
                return left + right;
            }

            public static string Combine(string left, string right)
            {
                return left + right;
            }
        }

        public static class Program
        {
            public static void Main()
            {
                int result = Calculator.Combine(1, 2);
                Console.WriteLine(result);
            }
        }
        """;
}
