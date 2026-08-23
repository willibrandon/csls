using Csls.Protocol;
using StreamJsonRpc;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies call hierarchies, type hierarchies, and inlay hints through a real worker.
/// </summary>
[TestClass]
public sealed class HierarchyAndInlayHintLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Returns exact semantic hierarchy relationships and resolvable inlay hints.
    /// </summary>
    [TestMethod]
    public async Task HierarchiesAndInlayHintsReturnSemanticResults()
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
            $"csls-hierarchies-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Hierarchy.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var lsp = LspProcessSession.Start(
                "csls-hierarchy-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement capabilities = initialization.GetProperty("capabilities");
            Assert.IsTrue(capabilities.GetProperty("callHierarchyProvider").GetBoolean());
            Assert.IsTrue(capabilities.GetProperty("typeHierarchyProvider").GetBoolean());
            Assert.IsTrue(
                capabilities
                    .GetProperty("inlayHintProvider")
                    .GetProperty("resolveProvider")
                    .GetBoolean());

            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);
            CallHierarchyItem runItem = await PrepareCallItemAsync(
                lsp,
                documentPath,
                new Position(22, 16)).ConfigureAwait(false);
            Assert.AreEqual("Run", runItem.Name);
            Assert.AreEqual(SymbolKind.Method, runItem.Kind);
            Assert.AreEqual(DocumentUri.FromFileSystemPath(documentPath), runItem.Uri);
            Assert.AreEqual(new Position(22, 16), runItem.SelectionRange.Start);

            IReadOnlyList<CallHierarchyIncomingCall> incomingCalls =
                await lsp.RequestIncomingCallsAsync(
                    runItem,
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(1, incomingCalls);
            CallHierarchyIncomingCall mainCaller = incomingCalls.Single(
                static call => call.From.Name == "Main");
            Assert.AreEqual(SymbolKind.Method, mainCaller.From.Kind);
            Assert.HasCount(1, mainCaller.FromRanges);
            Assert.AreEqual(37, mainCaller.FromRanges[0].Start.Line);
            Assert.AreEqual(new Position(37, 15), mainCaller.FromRanges[0].Start);
            Assert.AreEqual(new Position(37, 18), mainCaller.FromRanges[0].End);

            IReadOnlyList<CallHierarchyOutgoingCall> runOutgoing =
                await lsp.RequestOutgoingCallsAsync(
                    runItem,
                    TestContext.CancellationToken).ConfigureAwait(false);
            CallHierarchyOutgoingCall executeCall = runOutgoing.Single(
                static call => call.To.Name == "Execute");
            Assert.AreEqual(SymbolKind.Method, executeCall.To.Kind);
            Assert.HasCount(1, executeCall.FromRanges);
            Assert.AreEqual(new LspRange(new Position(24, 8), new Position(24, 17)),
                executeCall.FromRanges[0]);

            CallHierarchyItem executeItem = await PrepareCallItemAsync(
                lsp,
                documentPath,
                new Position(16, 25)).ConfigureAwait(false);
            IReadOnlyList<CallHierarchyOutgoingCall> executeOutgoing =
                await lsp.RequestOutgoingCallsAsync(
                    executeItem,
                    TestContext.CancellationToken).ConfigureAwait(false);
            CallHierarchyOutgoingCall helperCall = executeOutgoing.Single(
                static call => call.To.Name == "Helper");
            Assert.HasCount(2, helperCall.FromRanges);
            Assert.AreEqual(
                new LspRange(new Position(18, 8), new Position(18, 17)),
                helperCall.FromRanges[0]);
            Assert.AreEqual(
                new LspRange(new Position(19, 8), new Position(19, 24)),
                helperCall.FromRanges[1]);

            TypeHierarchyItem workerItem = await PrepareTypeItemAsync(
                lsp,
                documentPath,
                new Position(14, 20)).ConfigureAwait(false);
            Assert.AreEqual("Worker", workerItem.Name);
            Assert.AreEqual(SymbolKind.Class, workerItem.Kind);
            IReadOnlyList<TypeHierarchyItem> supertypes = await lsp.RequestSupertypesAsync(
                workerItem,
                TestContext.CancellationToken).ConfigureAwait(false);
            AssertStringSet(["BaseWorker", "IWorker"], supertypes.Select(static item => item.Name));

            TypeHierarchyItem baseItem = await PrepareTypeItemAsync(
                lsp,
                documentPath,
                new Position(7, 13)).ConfigureAwait(false);
            IReadOnlyList<TypeHierarchyItem> baseSubtypes = await lsp.RequestSubtypesAsync(
                baseItem,
                TestContext.CancellationToken).ConfigureAwait(false);
            AssertStringSet(
                ["IntermediateWorker", "Worker"],
                baseSubtypes.Select(static item => item.Name));

            TypeHierarchyItem interfaceItem = await PrepareTypeItemAsync(
                lsp,
                documentPath,
                new Position(2, 17)).ConfigureAwait(false);
            IReadOnlyList<TypeHierarchyItem> interfaceSubtypes =
                await lsp.RequestSubtypesAsync(
                    interfaceItem,
                    TestContext.CancellationToken).ConfigureAwait(false);
            AssertStringSet(
                ["IAdvancedWorker", "Worker"],
                interfaceSubtypes.Select(static item => item.Name));
            TypeHierarchyItem inferredVariableType = await PrepareTypeItemAsync(
                lsp,
                documentPath,
                new Position(36, 12)).ConfigureAwait(false);
            Assert.AreEqual("Worker", inferredVariableType.Name);
            Assert.AreEqual(workerItem.SelectionRange, inferredVariableType.SelectionRange);

            IReadOnlyList<InlayHint> hints = await lsp.RequestInlayHintsAsync(
                documentPath,
                new LspRange(new Position(0, 0), new Position(44, 0)),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(2, hints);
            InlayHint parameterHint = hints.Single(
                static hint => hint.Kind == InlayHintKind.Parameter);
            Assert.AreEqual("count:", parameterHint.Label);
            Assert.AreEqual(new Position(18, 15), parameterHint.Position);
            Assert.IsNotNull(parameterHint.TextEdits);
            Assert.HasCount(1, parameterHint.TextEdits);
            Assert.AreEqual("count: ", parameterHint.TextEdits[0].NewText);
            Assert.AreEqual(
                new LspRange(new Position(18, 15), new Position(18, 15)),
                parameterHint.TextEdits[0].Range);
            Assert.IsNull(parameterHint.Tooltip);

            InlayHint typeHint = hints.Single(static hint => hint.Kind == InlayHintKind.Type);
            Assert.AreEqual("Worker", typeHint.Label);
            Assert.AreEqual(new Position(36, 12), typeHint.Position);
            Assert.IsNotNull(typeHint.TextEdits);
            Assert.HasCount(1, typeHint.TextEdits);
            Assert.AreEqual("Worker", typeHint.TextEdits[0].NewText);
            Assert.AreEqual(
                new LspRange(new Position(36, 8), new Position(36, 11)),
                typeHint.TextEdits[0].Range);
            Assert.IsFalse(typeHint.PaddingLeft);
            Assert.IsTrue(typeHint.PaddingRight);

            IReadOnlyList<InlayHint> parameterRangeHints =
                await lsp.RequestInlayHintsAsync(
                    documentPath,
                    new LspRange(new Position(18, 0), new Position(19, 0)),
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(1, parameterRangeHints);
            Assert.AreEqual(InlayHintKind.Parameter, parameterRangeHints[0].Kind);
            IReadOnlyList<InlayHint> typeRangeHints = await lsp.RequestInlayHintsAsync(
                documentPath,
                new LspRange(new Position(36, 0), new Position(37, 0)),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(1, typeRangeHints);
            Assert.AreEqual(InlayHintKind.Type, typeRangeHints[0].Kind);
            IReadOnlyList<InlayHint> clampedRangeHints = await lsp.RequestInlayHintsAsync(
                documentPath,
                new LspRange(
                    new Position(0, 0),
                    new Position(int.MaxValue, int.MaxValue)),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(2, clampedRangeHints);
            AssertStringSet(
                ["Worker", "count:"],
                clampedRangeHints.Select(static hint => hint.Label));

            InlayHint resolvedParameter = await lsp.ResolveInlayHintAsync(
                parameterHint,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(resolvedParameter.Tooltip);
            Assert.Contains("int count", resolvedParameter.Tooltip.Value, StringComparison.Ordinal);
            Assert.Contains("Helper", resolvedParameter.Tooltip.Value, StringComparison.Ordinal);
            InlayHint resolvedType = await lsp.ResolveInlayHintAsync(
                typeHint,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(resolvedType.Tooltip);
            Assert.Contains("Fixture.Worker", resolvedType.Tooltip.Value, StringComparison.Ordinal);
            Assert.IsNotNull(resolvedType.TextEdits);
            Assert.HasCount(1, resolvedType.TextEdits);

            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = DocumentText + Environment.NewLine }])
                .ConfigureAwait(false);
            RemoteInvocationException staleResolve =
                await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
                    async () => await lsp.ResolveInlayHintAsync(
                        typeHint,
                        TestContext.CancellationToken).ConfigureAwait(false))
                    .ConfigureAwait(false);
            Assert.Contains(
                "workspace changed after the inlay hint",
                staleResolve.Message,
                StringComparison.OrdinalIgnoreCase);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private async Task<CallHierarchyItem> PrepareCallItemAsync(
        LspProcessSession lsp,
        string documentPath,
        Position position)
    {
        IReadOnlyList<CallHierarchyItem> items = await lsp.PrepareCallHierarchyAsync(
            documentPath,
            position,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.HasCount(1, items);
        Assert.IsNotNull(items[0].Data);
        return items[0];
    }

    private async Task<TypeHierarchyItem> PrepareTypeItemAsync(
        LspProcessSession lsp,
        string documentPath,
        Position position)
    {
        IReadOnlyList<TypeHierarchyItem> items = await lsp.PrepareTypeHierarchyAsync(
            documentPath,
            position,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.HasCount(1, items);
        Assert.IsNotNull(items[0].Data);
        return items[0];
    }

    private static void AssertStringSet(
        IReadOnlyList<string> expected,
        IEnumerable<string> actual)
    {
        string[] orderedExpected = [.. expected.Order(StringComparer.Ordinal)];
        string[] orderedActual = [.. actual.Order(StringComparer.Ordinal)];
        Assert.AreSequenceEqual(orderedExpected, orderedActual);
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

    private const string DocumentText = """
        namespace Fixture;

        public interface IWorker
        {
            void Run();
        }

        public class BaseWorker
        {
            public virtual void Execute()
            {
            }
        }

        public sealed class Worker : BaseWorker, IWorker
        {
            public override void Execute()
            {
                Helper(1);
                Helper(count: 2);
            }

            public void Run()
            {
                Execute();
            }

            private static void Helper(int count)
            {
            }
        }

        public static class Program
        {
            public static void Main()
            {
                var worker = new Worker();
                worker.Run();
                Consume(worker);
                Invoke(worker);
            }

            private static void Consume(Worker worker)
            {
            }

            private static void Invoke(IWorker worker)
            {
                worker.Run();
            }
        }

        public class IntermediateWorker : BaseWorker
        {
        }

        public sealed class LeafWorker : IntermediateWorker
        {
        }

        public interface IAdvancedWorker : IWorker
        {
        }

        public sealed class AdvancedWorker : IAdvancedWorker
        {
            public void Run()
            {
            }
        }
        """;
}
