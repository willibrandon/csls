using Csls.Protocol;
using Microsoft.CodeAnalysis.Text;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies move-to-file refactoring through a real language-server worker and SDK build.
/// </summary>
[TestClass]
public sealed class MoveTypeCodeActionLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Negotiates create-file support and returns an ordered refactoring that still builds.
    /// </summary>
    [TestMethod]
    public async Task MoveTypeToFileUsesOrderedResourceOperations()
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
            $"csls-move-type-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            string targetPath = Path.Join(fixturePath, "Helper.cs");
            string collisionPath = Path.Join(fixturePath, "CollisionTypes.cs");
            string reservedPath = Path.Join(fixturePath, "ReservedTypes.cs");
            string blockPath = Path.Join(fixturePath, "BlockTypes.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                collisionPath,
                CollisionDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "collisionhelper.cs"),
                ExistingCollisionText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                reservedPath,
                ReservedDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                blockPath,
                BlockNamespaceDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            await VerifyUnsupportedClientAsync(workerPath, fixturePath, documentPath)
                .ConfigureAwait(false);

            using var capabilities = JsonDocument.Parse(
                """
                {
                  "workspace": {
                    "workspaceEdit": {
                      "documentChanges": true,
                      "resourceOperations": ["create"]
                    }
                  }
                }
                """);
            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-move-type-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                capabilities.RootElement,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains(
                "refactor",
                initialization
                    .GetProperty("capabilities")
                    .GetProperty("codeActionProvider")
                    .GetProperty("codeActionKinds")
                    .EnumerateArray()
                    .Select(static kind => kind.GetString()));
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);

            IReadOnlyList<CodeAction> actions = await lsp.RequestCodeActionsAsync(
                documentPath,
                new LspRange(new Position(7, 22), new Position(7, 28)),
                ["refactor"],
                TestContext.CancellationToken).ConfigureAwait(false);
            CodeAction action = Assert.ContainsSingle(actions.Where(static action =>
                action.Title == "Move Helper to Helper.cs"));
            Assert.AreEqual("Move Helper to Helper.cs", action.Title);
            Assert.AreEqual("refactor", action.Kind);
            Assert.IsTrue(action.IsPreferred);
            WorkspaceEdit edit = action.Edit
                ?? throw new InvalidDataException("The move-to-file action had no edit.");
            Assert.HasCount(3, edit.DocumentChanges);

            CreateFile createFile = edit.DocumentChanges[0] as CreateFile
                ?? throw new InvalidDataException("The first change did not create the target file.");
            Assert.AreEqual(DocumentUri.FromFileSystemPath(targetPath), createFile.Uri);
            TextDocumentEdit targetEdit = edit.DocumentChanges[1] as TextDocumentEdit
                ?? throw new InvalidDataException("The second change did not populate the target file.");
            Assert.AreEqual(DocumentUri.FromFileSystemPath(targetPath), targetEdit.TextDocument.Uri);
            Assert.IsNull(targetEdit.TextDocument.Version);
            TextDocumentEdit sourceEdit = edit.DocumentChanges[2] as TextDocumentEdit
                ?? throw new InvalidDataException("The third change did not update the source file.");
            Assert.AreEqual(DocumentUri.FromFileSystemPath(documentPath), sourceEdit.TextDocument.Uri);
            Assert.AreEqual(1, sourceEdit.TextDocument.Version);

            string movedText = ApplyTextEdits(string.Empty, targetEdit.Edits);
            string remainingText = ApplyTextEdits(DocumentText, sourceEdit.Edits);
            Assert.Contains("internal static class Helper", movedText, StringComparison.Ordinal);
            Assert.DoesNotContain("class Program", movedText, StringComparison.Ordinal);
            Assert.Contains("public static class Program", remainingText, StringComparison.Ordinal);
            Assert.DoesNotContain("class Helper", remainingText, StringComparison.Ordinal);

            IReadOnlyList<CodeAction> collisionActions = await lsp.RequestCodeActionsAsync(
                collisionPath,
                new LspRange(new Position(7, 22), new Position(7, 37)),
                ["refactor"],
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "Move CollisionHelper to CollisionHelper.cs",
                collisionActions.Select(static action => action.Title));
            IReadOnlyList<CodeAction> reservedActions = await lsp.RequestCodeActionsAsync(
                reservedPath,
                new LspRange(new Position(7, 22), new Position(7, 25)),
                ["refactor"],
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "Move Con to Con.cs",
                reservedActions.Select(static action => action.Title));
            IReadOnlyList<CodeAction> blockActions = await lsp.RequestCodeActionsAsync(
                blockPath,
                new LspRange(new Position(7, 27), new Position(7, 34)),
                ["refactor"],
                TestContext.CancellationToken).ConfigureAwait(false);
            CodeAction blockAction = Assert.ContainsSingle(blockActions.Where(static action =>
                action.Title == "Move Payload to Payload.cs"));
            WorkspaceEdit blockEdit = blockAction.Edit
                ?? throw new InvalidDataException("The block-namespace action had no edit.");
            TextDocumentEdit blockTargetEdit = blockEdit.DocumentChanges[1] as TextDocumentEdit
                ?? throw new InvalidDataException(
                    "The block-namespace action did not populate its target file.");
            string blockTargetText = ApplyTextEdits(string.Empty, blockTargetEdit.Edits);
            Assert.Contains("namespace Fixture", blockTargetText, StringComparison.Ordinal);
            Assert.Contains("record Payload", blockTargetText, StringComparison.Ordinal);
            Assert.DoesNotContain("class BlockTypes", blockTargetText, StringComparison.Ordinal);

            await File.WriteAllTextAsync(
                targetPath,
                movedText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                remainingText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await RunDotNetBuildAsync(fixturePath, TestContext.CancellationToken)
                .ConfigureAwait(false);

            string workerDiagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "Unhandled exception",
                workerDiagnostics,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private async Task VerifyUnsupportedClientAsync(
        string workerPath,
        string fixturePath,
        string documentPath)
    {
        LspProcessSession lsp = await LspProcessSession.StartAsync(
            "csls-move-type-unsupported-worker",
            EditorToolResolver.ResolveDotNetHost(),
            [workerPath],
            fixturePath).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
        JsonElement initialization = await lsp.InitializeAsync(
            fixturePath,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains(
            "refactor",
            initialization
                .GetProperty("capabilities")
                .GetProperty("codeActionProvider")
                .GetProperty("codeActionKinds")
                .EnumerateArray()
                .Select(static kind => kind.GetString()));
        await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);
        IReadOnlyList<CodeAction> actions = await lsp.RequestCodeActionsAsync(
            documentPath,
            new LspRange(new Position(7, 22), new Position(7, 28)),
            ["refactor"],
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.DoesNotContain(
            "Move Helper to Helper.cs",
            actions.Select(static action => action.Title));
        await lsp.ShutdownAsync(TestContext.CancellationToken).ConfigureAwait(false);
    }

    private static string ApplyTextEdits(string text, IReadOnlyList<TextEdit> edits)
    {
        var sourceText = SourceText.From(text, Encoding.UTF8);
        IEnumerable<TextChange> changes = edits.Select(edit => new TextChange(
            TextSpan.FromBounds(
                GetOffset(sourceText, edit.Range.Start),
                GetOffset(sourceText, edit.Range.End)),
            edit.NewText));
        return sourceText.WithChanges(changes).ToString();
    }

    private static int GetOffset(SourceText text, Position position) =>
        text.Lines[position.Line].Start + position.Character;

    private static async Task RunDotNetBuildAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = EditorToolResolver.ResolveDotNetHost(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--verbosity");
        startInfo.ArgumentList.Add("quiet");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        startInfo.Environment["DOTNET_NOLOGO"] = "true";
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The test .NET build process did not start.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string output = await standardOutput.ConfigureAwait(false);
        string error = await standardError.ConfigureAwait(false);
        Assert.AreEqual(
            0,
            process.ExitCode,
            $"dotnet build failed:{Environment.NewLine}{error}{Environment.NewLine}{output}");
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

        public static class Program
        {
            public static int Read() => Helper.Value;
        }

        internal static class Helper
        {
            public static int Value => 42;
        }
        """;

    private const string CollisionDocumentText = """
        namespace Fixture;

        public static class CollisionTypes
        {
            public static int Read() => CollisionHelper.Value;
        }

        internal static class CollisionHelper
        {
            public static int Value => 42;
        }
        """;

    private const string ExistingCollisionText = """
        namespace Fixture;

        internal static class ExistingCollision;
        """;

    private const string ReservedDocumentText = """
        namespace Fixture;

        public static class ReservedTypes
        {
            public static int Read() => CON.Value;
        }

        internal static class CON
        {
            public static int Value => 42;
        }
        """;

    private const string BlockNamespaceDocumentText = """
        namespace Fixture
        {
            public static class BlockTypes
            {
                public static int Read() => new Payload(42).Value;
            }

            internal sealed record Payload(int Value);
        }
        """;
}
